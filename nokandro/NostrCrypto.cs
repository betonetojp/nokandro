using System.Security.Cryptography;
using System.Text;
using NBitcoin.Secp256k1;

namespace nokandro
{
    // High-performance and timing-attack-safe Secp256k1 / Schnorr implementation for Nostr
    // Powered by NBitcoin.Secp256k1 library.
    public static class NostrCrypto
    {
        private static readonly Context SecpContext = Context.Instance;

        public static byte[] GetPublicKey(byte[] privKey)
        {
            if (privKey.Length != 32) throw new ArgumentException("Invalid private key length");
            using var key = ECPrivKey.Create(privKey);
            var pubkey = key.CreateXOnlyPubKey();
            return pubkey.ToBytes();
        }

        // --- Encryption (NIP-04) ---

        public static string EncryptNip04(string plaintext, string pubkeyHex, byte[] privKey)
        {
            var sharedSecret = GetSharedSecret(HexStringToBytes(pubkeyHex), privKey);
            if (sharedSecret == null) throw new InvalidOperationException("Failed to compute shared secret");

            using var aes = Aes.Create();
            aes.Key = sharedSecret;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            return Convert.ToBase64String(cipherBytes) + "?iv=" + Convert.ToBase64String(aes.IV);
        }

        // --- Encryption (NIP-44 v2) ---

        public static string EncryptNip44(string plaintext, string pubkeyHex, byte[] privKey)
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            if (plaintextBytes.Length == 0) throw new ArgumentException("Plaintext cannot be empty");

            var sharedSecret = GetSharedSecret(HexStringToBytes(pubkeyHex), privKey);
            if (sharedSecret == null) throw new InvalidOperationException("Failed to compute shared secret");

            // Conversation key
            var salt = Encoding.UTF8.GetBytes("nip44-v2");
            var conversationKey = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, salt);

            // Random nonce (32 bytes)
            var nonce = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);

            // Derive keys: chacha_key(32) + chacha_nonce(12) + hmac_key(32) = 76 bytes
            var keys = new byte[76];
            HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, keys, nonce);
            var chachaKey = keys.AsSpan(0, 32);
            var chachaNonce = keys.AsSpan(32, 12);
            var hmacKey = keys.AsSpan(44, 32);

            // Pad: 2-byte BE length prefix + plaintext + zero padding
            var paddedLen = CalcPaddedLen(plaintextBytes.Length);
            var padded = new byte[2 + paddedLen];
            padded[0] = (byte)(plaintextBytes.Length >> 8);
            padded[1] = (byte)(plaintextBytes.Length & 0xFF);
            Array.Copy(plaintextBytes, 0, padded, 2, plaintextBytes.Length);

            // Encrypt with ChaCha20
            var ciphertext = ChaCha20Transform(chachaKey, chachaNonce, padded);

            // HMAC-SHA256(hmac_key, nonce || ciphertext)
            var hmacData = new byte[32 + ciphertext.Length];
            Array.Copy(nonce, 0, hmacData, 0, 32);
            Array.Copy(ciphertext, 0, hmacData, 32, ciphertext.Length);
            using var hmac = new System.Security.Cryptography.HMACSHA256(hmacKey.ToArray());
            var mac = hmac.ComputeHash(hmacData);

            // Assemble: version(1) + nonce(32) + ciphertext + mac(32)
            var payload = new byte[1 + 32 + ciphertext.Length + 32];
            payload[0] = 0x02;
            Array.Copy(nonce, 0, payload, 1, 32);
            Array.Copy(ciphertext, 0, payload, 33, ciphertext.Length);
            Array.Copy(mac, 0, payload, 33 + ciphertext.Length, 32);

            return Convert.ToBase64String(payload);
        }

        private static int CalcPaddedLen(int len)
        {
            if (len <= 0) throw new ArgumentException("Length must be positive");
            if (len <= 32) return 32;
            var nextPower = 1 << ((int)Math.Floor(Math.Log2(len - 1)) + 1);
            var chunk = nextPower <= 256 ? 32 : nextPower / 8;
            return chunk * ((len - 1) / chunk + 1);
        }

        // --- Decryption (NIP-04 & NIP-44) ---

        public static string? Decrypt(string content, string pubkeyHex, byte[] privKey)
        {
            if (content.Contains("?iv="))
            {
                return DecryptNip04(content, pubkeyHex, privKey);
            }
            if (!string.IsNullOrEmpty(content) && content.Length > 1)
            {
                return DecryptNip44(content, pubkeyHex, privKey);
            }
            return null;
        }

        private static string? DecryptNip04(string content, string pubkeyHex, byte[] privKey)
        {
            try
            {
                var parts = content.Split("?iv=");
                if (parts.Length != 2) return null;
                var ciphertext = parts[0];
                var ivBase64 = parts[1];
                
                var sharedSecret = GetSharedSecret(HexStringToBytes(pubkeyHex), privKey);
                if (sharedSecret == null) return null;

                using var aes = Aes.Create();
                aes.Key = sharedSecret;
                aes.IV = Convert.FromBase64String(ivBase64);
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                var cipherBytes = Convert.FromBase64String(ciphertext);
                var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch 
            {
                return null;
            }
        }

        public static string? DecryptNip44(string content, string pubkeyHex, byte[] privKey)
        {
            try
            {
                var payload = Convert.FromBase64String(content);
                if (payload.Length < 66) return null;
                if (payload[0] != 0x02) return null;

                var nonce = payload.AsSpan(1, 32);
                var ciphertextWithMac = payload.AsSpan(33);
                if (ciphertextWithMac.Length < 33) return null;

                var mac = ciphertextWithMac[^32..];
                var ciphertext = ciphertextWithMac[..^32];

                var sharedSecret = GetSharedSecret(HexStringToBytes(pubkeyHex), privKey);
                if (sharedSecret == null) return null;

                var salt = Encoding.UTF8.GetBytes("nip44-v2");
                var conversationKey = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, salt);

                var keys = new byte[76];
                HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, keys, nonce);

                var chachaKey = keys.AsSpan(0, 32);
                var chachaNonce = keys.AsSpan(32, 12);
                var hmacKey = keys.AsSpan(44, 32);

                // Verify HMAC-SHA256(hmac_key, nonce || ciphertext)
                var hmacData = new byte[32 + ciphertext.Length];
                nonce.CopyTo(hmacData.AsSpan(0, 32));
                ciphertext.CopyTo(hmacData.AsSpan(32));

                using var hmacSha = new System.Security.Cryptography.HMACSHA256(hmacKey.ToArray());
                var expectedMac = hmacSha.ComputeHash(hmacData);
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, mac))
                    return null;

                // Decrypt with plain ChaCha20
                var paddedPlaintext = ChaCha20Transform(chachaKey, chachaNonce, ciphertext);

                // Unpad
                if (paddedPlaintext.Length < 2) return null;
                int plaintextLen = (paddedPlaintext[0] << 8) | paddedPlaintext[1];
                if (plaintextLen <= 0 || plaintextLen > paddedPlaintext.Length - 2) return null;

                return Encoding.UTF8.GetString(paddedPlaintext, 2, plaintextLen);
            }
            catch
            {
                return null;
            }
        }

        // --- ChaCha20 stream cipher (RFC 8439) ---

        private static byte[] ChaCha20Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> data)
        {
            var output = new byte[data.Length];
            Span<byte> block = stackalloc byte[64];
            uint counter = 0;

            for (int offset = 0; offset < data.Length; offset += 64)
            {
                ChaCha20Block(key, nonce, counter, block);
                counter++;

                int len = Math.Min(64, data.Length - offset);
                for (int i = 0; i < len; i++)
                    output[offset + i] = (byte)(data[offset + i] ^ block[i]);
            }

            return output;
        }

        private static void ChaCha20Block(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> output)
        {
            Span<uint> state = stackalloc uint[16];
            state[0] = 0x61707865;
            state[1] = 0x3320646e;
            state[2] = 0x79622d32;
            state[3] = 0x6b206574;

            for (int i = 0; i < 8; i++)
                state[4 + i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));

            state[12] = counter;

            for (int i = 0; i < 3; i++)
                state[13 + i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(i * 4, 4));

            Span<uint> working = stackalloc uint[16];
            state.CopyTo(working);

            for (int i = 0; i < 10; i++)
            {
                QuarterRound(ref working[0], ref working[4], ref working[8], ref working[12]);
                QuarterRound(ref working[1], ref working[5], ref working[9], ref working[13]);
                QuarterRound(ref working[2], ref working[6], ref working[10], ref working[14]);
                QuarterRound(ref working[3], ref working[7], ref working[11], ref working[15]);
                QuarterRound(ref working[0], ref working[5], ref working[10], ref working[15]);
                QuarterRound(ref working[1], ref working[6], ref working[11], ref working[12]);
                QuarterRound(ref working[2], ref working[7], ref working[8], ref working[13]);
                QuarterRound(ref working[3], ref working[4], ref working[9], ref working[14]);
            }

            for (int i = 0; i < 16; i++)
            {
                working[i] += state[i];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), working[i]);
            }
        }

        private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            a += b; d ^= a; d = uint.RotateLeft(d, 16);
            c += d; b ^= c; b = uint.RotateLeft(b, 12);
            a += b; d ^= a; d = uint.RotateLeft(d, 8);
            c += d; b ^= c; b = uint.RotateLeft(b, 7);
        }

        private static byte[]? GetSharedSecret(byte[] pubKeyX, byte[] privKey)
        {
            try
            {
                if (pubKeyX.Length != 32) return null;
                if (privKey.Length != 32) return null;

                using var key = ECPrivKey.Create(privKey);
                
                // Use 0x02 + X-coordinate (33 bytes) to parse with ECPubKey.TryCreate
                var compressedPub = new byte[33];
                compressedPub[0] = 0x02;
                Array.Copy(pubKeyX, 0, compressedPub, 1, 32);

                if (!ECPubKey.TryCreate(compressedPub, SecpContext, out bool parity, out var pubkey) || pubkey == null)
                    return null;

                var sharedPoint = pubkey.GetSharedPubkey(key);
                if (sharedPoint == null) return null;

                var ecdhBuf = new byte[33];
                sharedPoint.WriteToSpan(true, ecdhBuf, out _);

                var res = new byte[32];
                Array.Copy(ecdhBuf, 1, res, 0, 32);
                return res;
            }
            catch
            {
                return null;
            }
        }

        public static byte[] Sign(byte[] messageHash, byte[] privKey)
        {
            if (privKey.Length != 32) throw new ArgumentException("Invalid privkey");
            if (messageHash.Length != 32) throw new ArgumentException("Invalid msg hash");

            using var key = ECPrivKey.Create(privKey);
            var signature = key.SignBIP340(messageHash);
            if (signature == null)
            {
                throw new InvalidOperationException("Failed to generate Schnorr signature");
            }

            var sigBytes = new byte[64];
            signature.WriteToSpan(sigBytes);
            return sigBytes;
        }

        private static byte[] HexStringToBytes(string hex)
        {
             if (hex.Length % 2 != 0) return new byte[0];
             var bytes = new byte[hex.Length / 2];
             for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
             return bytes;
        }
    }
}
