using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace nokandro
{
    // Minimal Secp256k1 / Schnorr implementation for Nostr
    // Based on BIP-340 reference and simplified for C# BigInteger
    public static class NostrCrypto
    {
        // Secp256k1 parameters
        private static readonly BigInteger P = BigInteger.Parse("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F", System.Globalization.NumberStyles.HexNumber);
        private static readonly BigInteger N = BigInteger.Parse("00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);
        private static readonly BigInteger Gx = BigInteger.Parse("0079BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798", System.Globalization.NumberStyles.HexNumber);
        private static readonly BigInteger Gy = BigInteger.Parse("00483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8", System.Globalization.NumberStyles.HexNumber);

        public class Point
        {
            public BigInteger X { get; }
            public BigInteger Y { get; }
            public bool IsInfinity { get; }

            public Point(BigInteger x, BigInteger y)
            {
                X = x;
                Y = y;
                IsInfinity = false;
            }
            private Point() { IsInfinity = true; } // Infinity
            public static readonly Point Infinity = new Point();
        }
        
        // G point
        private static readonly Point G = new Point(Gx, Gy);

        // a = 0, b = 7 for secp256k1 (y^2 = x^3 + 7)
        
        public static byte[] GetPublicKey(byte[] privKey)
        {
            if (privKey.Length != 32) throw new ArgumentException("Invalid private key length");
            var d = BytesToBigInt(privKey);
            if (d <= 0 || d >= N) throw new ArgumentException("Invalid private key range");
            
            var P = Multiply(G, d);
            // BIP-340: return x-coordinate of P, 32 bytes
            return BigIntToBytes(P.X, 32);
        }

        // --- NIP-04 Encryption/Decryption ---

        public static string? Decrypt(string content, string pubkeyHex, byte[] privKey)
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

        private static byte[]? GetSharedSecret(byte[] pubKeyX, byte[] privKey)
        {
            try
            {
                var P_B = LiftX(pubKeyX);
                if (P_B.IsInfinity) return null;

                var d = BytesToBigInt(privKey);
                var S = Multiply(P_B, d);

                if (S.IsInfinity) return null;
                return BigIntToBytes(S.X, 32);
            }
            catch { return null; }
        }

        private static Point LiftX(byte[] xBytes)
        {
            if (xBytes.Length != 32) return Point.Infinity;
            var x = BytesToBigInt(xBytes);
            if (x >= P) return Point.Infinity;

            // y^2 = x^3 + 7
            var ySq = (BigInteger.ModPow(x, 3, P) + 7) % P;
            var y = ModSqrt(ySq, P);
            
            if (y == -1) return Point.Infinity; // no sqrt

            // Enforce even Y
            if (y % 2 != 0) y = P - y;

            return new Point(x, y);
        }

        private static BigInteger ModSqrt(BigInteger a, BigInteger p)
        {
            // For P = 3 mod 4, sqrt(a) = a^((p+1)/4) mod p
            // 2^256 - 2^32 - 977 is 3 mod 4.
            // Check legendre symbol (a^((p-1)/2)) to ensure root exists? 
            if (BigInteger.ModPow(a, (p - 1) / 2, p) != 1) return -1;
            
            return BigInteger.ModPow(a, (p + 1) / 4, p);
        }

        private static byte[] HexStringToBytes(string hex)
        {
             if (hex.Length % 2 != 0) return new byte[0];
             var bytes = new byte[hex.Length / 2];
             for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
             return bytes;
        }

        // Helper to get normalized public key bytes and verify evenness (optional)
        private static Point GetPoint(byte[] privKey)
        {
            var d = BytesToBigInt(privKey);
            return Multiply(G, d);
        }

        public static byte[] Sign(byte[] messageHash, byte[] privKey)
        {
             // BIP-340 Signing
             // Input: 
             //   sk: 32-byte secret key
             //   m: 32-byte message
             //   a: 32-byte aux random (we will use random bytes)
             
             if (privKey.Length != 32) throw new ArgumentException("Invalid privkey");
             if (messageHash.Length != 32) throw new ArgumentException("Invalid msg hash");

             var d0 = BytesToBigInt(privKey);
             if (d0 <= 0 || d0 >= N) throw new ArgumentException("Invalid privkey range");

             var P = Multiply(G, d0);
             BigInteger d = d0;
             if (P.Y % 2 != 0) 
             {
                 d = N - d;
             }
             
             // t = xor(bytes(d), TaggedHash("BIP0340/aux", a))[0..32] ... we can skip aux or just use random for nonce
             // Simple deterministic nonce generation or random? 
             // BIP-340 recommends: let t be the byte-wise xor of bytes(d) and hash_aux(a)
             // Let rand = hash_nonce(t || bytes(P) || m)
             // k = int(rand) mod n
             // Fail if k = 0
             // Let R = kG
             // let k = k if has_even_y(R) else n - k
             // e = int(hash_challenge(bytes(x(R)) || bytes(x(P)) || m)) mod n
             // sig = bytes(x(R)) || bytes((k + ed) mod n)
             
             // We implement simplified nonce generation using random to avoid implementing full tagged hash for aux
             // But deterministic is safer. Let's use RFC6979-like or just Random.
             // For safety in this minimal implementation, let's use RNG.
             
             var rand = new byte[32];
             using (var rng = RandomNumberGenerator.Create())
             {
                 rng.GetBytes(rand);
             }
             
             // Reuse tagged hash for challenge
             // But first we need k
             // We'll treat 'rand' as k directly for simplicity (NOT RFC6979 deterministic, but valid Schnorr scheme)
             // IMPORTANT: Valid Schnorr signature requires strong k (nonce).
             // Using system secure random is acceptable for security if RNG is good.
             
             var k = BytesToBigInt(rand);
             k = k % N;
             if (k == 0) k = 1; // extremely unlikely

             var R = Multiply(G, k);
             if (R.Y % 2 != 0)
             {
                 k = N - k;
                 // R y becomes even (R corresponds to n-k)
                 // R x stays same
             }

             var rx = BigIntToBytes(R.X, 32);
             var px = BigIntToBytes(P.X, 32);
             // e = hash(R.x || P.x || m)
             // Nostr uses standard BIP-340 challenge?
             // Nostr NIP-01 says: "The signature is valid over the 32-byte sha256 hash of the serialized event data."
             // "Schnorr signature of the sha256 hash of the serialized event data"
             // BIP-340 challenge is TaggedHash("BIP0340/challenge", rx || px || m)
             
             var challengeData = new byte[32 + 32 + 32];
             Array.Copy(rx, 0, challengeData, 0, 32);
             Array.Copy(px, 0, challengeData, 32, 32);
             Array.Copy(messageHash, 0, challengeData, 64, 32);
             
             var eBytes = TaggedHash("BIP0340/challenge", challengeData);
             var e = BytesToBigInt(eBytes) % N;
             
             var s = (k + e * d) % N;
             
             var sig = new byte[64];
             Array.Copy(rx, 0, sig, 0, 32);
             var sBytes = BigIntToBytes(s, 32);
             Array.Copy(sBytes, 0, sig, 32, 32);
             
             return sig;
        }

        private static byte[] TaggedHash(string tag, byte[] data)
        {
            using var sha = SHA256.Create();
            var tagBytes = Encoding.UTF8.GetBytes(tag);
            var tagHash = sha.ComputeHash(tagBytes);
            
            var buffer = new byte[32 + 32 + data.Length];
            Array.Copy(tagHash, 0, buffer, 0, 32);
            Array.Copy(tagHash, 0, buffer, 32, 32);
            Array.Copy(data, 0, buffer, 64, data.Length);
            
            return sha.ComputeHash(buffer);
        }

        // --- BigInteger Helpers ---
        
        private static BigInteger BytesToBigInt(byte[] bytes)
        {
            // BigInteger expects little endian, and may be negative if msb is set.
            // We need positive big integer from big-endian bytes.
            // Pad with a zero byte at the end (little endian MSB) to ensure positive.
            var rev = new byte[bytes.Length + 1];
            for (int i = 0; i < bytes.Length; i++) rev[bytes.Length - 1 - i] = bytes[i];
            rev[bytes.Length] = 0; // force positive
            return new BigInteger(rev);
        }

        private static byte[] BigIntToBytes(BigInteger b, int len)
        {
            var raw = b.ToByteArray(); // little endian
            var res = new byte[len];
            for (int i = 0; i < len; i++)
            {
                if (i < raw.Length) res[len - 1 - i] = raw[i];
                else res[len - 1 - i] = 0; // pad
            }
            if (raw.Length > len && raw[raw.Length-1] == 0) 
            {
                // ignore padding zero if exists and fits
            }
            // Better logic: big endian result
            // e.g. val=1 -> raw=[1], len=32 -> res=[0...01]
            return res;
        }

        // --- EC Math ---
        
        private static Point Multiply(Point p, BigInteger k)
        {
            // Double-and-add
            Point r = Point.Infinity;
            Point curr = p;
            
            // process bits
            // BigInteger has no direct bit length, use ToByteArray (little endian)
            var bytes = k.ToByteArray();
            for (int i = 0; i < bytes.Length * 8; i++)
            {
                if ((k & (BigInteger.One << i)) != 0)
                {
                    r = Add(r, curr);
                }
                curr = Double(curr);
            }
            return r;
        }

        private static Point Add(Point p1, Point p2)
        {
            if (p1.IsInfinity) return p2;
            if (p2.IsInfinity) return p1;
            
            if (p1.X == p2.X)
            {
                if (p1.Y == p2.Y) return Double(p1);
                return Point.Infinity;
            }
            
            var slope = (p2.Y - p1.Y) * ModInverse(p2.X - p1.X, P);
            slope = slope % P;
            if (slope < 0) slope += P;
            
            var x3 = (slope * slope - p1.X - p2.X) % P;
            if (x3 < 0) x3 += P;
            
            var y3 = (slope * (p1.X - x3) - p1.Y) % P;
            if (y3 < 0) y3 += P;
            
            return new Point(x3, y3);
        }

        private static Point Double(Point p)
        {
            if (p.IsInfinity) return p;
            
            var slope = (3 * p.X * p.X) * ModInverse(2 * p.Y, P);
            slope = slope % P;
            if (slope < 0) slope += P;
            
            var x3 = (slope * slope - 2 * p.X) % P;
            if (x3 < 0) x3 += P;
            
            var y3 = (slope * (p.X - x3) - p.Y) % P;
            if (y3 < 0) y3 += P;
            
            return new Point(x3, y3);
        }

        private static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            // Extended Euclidean
            // Or since P is prime, a^(m-2) mod m
            return BigInteger.ModPow(a, m - 2, m);
        }
    }
}
