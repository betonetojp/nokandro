using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace nokandro
{
    public static class NostrKeyDecoder
    {
        private const string BECH32_CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
        private static readonly uint[] BECH32_GENERATOR = [0x3b6a57b2u, 0x26508e6du, 0x1ea119fau, 0x3d4233ddu, 0x2a1462b3u];

        public static (string? hrp, byte[]? data) Bech32Decode(string bech)
        {
            if (string.IsNullOrEmpty(bech)) return (null, null);
            bech = bech.ToLowerInvariant();
            var pos = bech.LastIndexOf('1');
            if (pos < 1 || pos + 7 > bech.Length) return (null, null);
            var hrp = bech[..pos];
            var dataPart = bech[(pos + 1)..];
            var data = new byte[dataPart.Length];
            for (int i = 0; i < dataPart.Length; i++)
            {
                var idx = BECH32_CHARSET.IndexOf(dataPart[i]);
                if (idx == -1) return (null, null);
                data[i] = (byte)idx;
            }

            var hrpExpanded = HrpExpand(hrp);
            var values = new List<uint>();
            foreach (var v in hrpExpanded) values.Add(v);
            foreach (var d in data) values.Add(d);
            if (Bech32Polymod([.. values]) != 1u) return (null, null);

            var payload = new byte[data.Length - 6];
            Array.Copy(data, 0, payload, 0, payload.Length);
            return (hrp, payload);
        }

        public static string Bech32Encode(string hrp, byte[] data)
        {
            var combined = new List<byte>(data);
            var checksum = CreateChecksum(hrp, data);
            combined.AddRange(checksum);
            var sb = new StringBuilder();
            sb.Append(hrp);
            sb.Append('1');
            foreach (var b in combined)
            {
                sb.Append(BECH32_CHARSET[b]);
            }
            return sb.ToString();
        }

        private static byte[] CreateChecksum(string hrp, byte[] data)
        {
            var hrpExp = HrpExpand(hrp);
            var values = new List<uint>();
            foreach (var v in hrpExp) values.Add(v);
            foreach (var d in data) values.Add(d);
            for (int i = 0; i < 6; i++) values.Add(0);
            var polymod = Bech32Polymod([.. values]) ^ 1u;
            var checksum = new byte[6];
            for (int i = 0; i < 6; i++) checksum[i] = (byte)((polymod >> (5 * (5 - i))) & 0x1fu);
            return checksum;
        }

        public static byte[]? ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
        {
            var acc = 0;
            var bits = 0;
            var maxv = (1 << toBits) - 1;
            var result = new List<byte>();
            foreach (var value in data)
            {
                if ((value >> fromBits) != 0) return null;
                acc = (acc << fromBits) | value;
                bits += fromBits;
                while (bits >= toBits)
                {
                    bits -= toBits;
                    result.Add((byte)((acc >> bits) & maxv));
                }
            }

            if (pad)
            {
                if (bits > 0) result.Add((byte)((acc << (toBits - bits)) & maxv));
            }
            else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
            {
                return null;
            }

            return [.. result];
        }

        private static uint[] HrpExpand(string hrp)
        {
            var exp = new List<uint>();
            foreach (var c in hrp) exp.Add((uint)(c >> 5));
            exp.Add(0);
            foreach (var c in hrp) exp.Add((uint)(c & 31));
            return [.. exp];
        }

        private static uint Bech32Polymod(uint[] values)
        {
            uint chk = 1;
            foreach (var v in values)
            {
                var top = chk >> 25;
                chk = ((chk & 0x1ffffffu) << 5) ^ v;
                for (int i = 0; i < 5; i++)
                {
                    if (((top >> i) & 1) == 1) chk ^= BECH32_GENERATOR[i];
                }
            }
            return chk;
        }

        public static bool TryDecodeNsecToHex(string nsec, out string? nsecHex)
        {
            nsecHex = null;
            try
            {
                if (string.IsNullOrWhiteSpace(nsec) || !nsec.StartsWith("nsec1", StringComparison.OrdinalIgnoreCase)) return false;
                var (hrp, data) = Bech32Decode(nsec.Trim());
                if (hrp != "nsec" || data == null) return false;
                var priv = ConvertBits(data, 5, 8, false);
                if (priv == null || priv.Length != 32) return false;
                nsecHex = BitConverter.ToString(priv).Replace("-", string.Empty).ToLowerInvariant();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string? DecodeBech32Npub(string npub)
        {
            try
            {
                var (hrp, data) = Bech32Decode(npub);
                if (hrp == "npub" && data != null)
                {
                    var pub = ConvertBits(data, 5, 8, false);
                    if (pub != null && pub.Length == 32)
                        return BitConverter.ToString(pub).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { }
            return null;
        }

        public static string? NormalizeHexPubkey(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            var clean = hex.Trim().ToLowerInvariant();
            if (clean.Length == 64 && Regex.IsMatch(clean, "^[a-f0-9]{64}$"))
                return clean;
            return null;
        }

        public static string EncodeHexToNpub(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            var data = ConvertBits(bytes, 8, 5, true) ?? throw new ArgumentException("Invalid hex");
            return Bech32Encode("npub", data);
        }

        public static string? EncodeHexToNote(string hexEventId)
        {
            try
            {
                var norm = NormalizeHexPubkey(hexEventId);
                if (norm == null) return null;
                var bytes = new byte[32];
                for (int i = 0; i < 32; i++) bytes[i] = Convert.ToByte(norm.Substring(i * 2, 2), 16);
                var data = ConvertBits(bytes, 8, 5, true);
                if (data == null) return null;
                return Bech32Encode("note", data);
            }
            catch
            {
                return null;
            }
        }

        public static string? EncodeNevent(string hexEventId, string? relayUrl = null, string? authorHex = null, int? kind = 1)
        {
            try
            {
                var normId = NormalizeHexPubkey(hexEventId);
                if (normId == null) return null;

                var tlv = new List<byte>();

                // Type 0: special (32-byte event id)
                tlv.Add(0);
                tlv.Add(32);
                for (int i = 0; i < 32; i++) tlv.Add(Convert.ToByte(normId.Substring(i * 2, 2), 16));

                // Type 1: relay (ASCII)
                if (!string.IsNullOrWhiteSpace(relayUrl))
                {
                    var relayBytes = Encoding.ASCII.GetBytes(relayUrl.Trim());
                    if (relayBytes.Length > 0 && relayBytes.Length <= 255)
                    {
                        tlv.Add(1);
                        tlv.Add((byte)relayBytes.Length);
                        tlv.AddRange(relayBytes);
                    }
                }

                // Type 2: author (32-byte pubkey)
                var normAuthor = !string.IsNullOrWhiteSpace(authorHex) ? NormalizeHexPubkey(authorHex!) : null;
                if (normAuthor != null)
                {
                    tlv.Add(2);
                    tlv.Add(32);
                    for (int i = 0; i < 32; i++) tlv.Add(Convert.ToByte(normAuthor.Substring(i * 2, 2), 16));
                }

                // Type 3: kind (32-bit unsigned integer, big-endian)
                if (kind.HasValue && kind.Value >= 0)
                {
                    uint k = (uint)kind.Value;
                    tlv.Add(3);
                    tlv.Add(4);
                    tlv.Add((byte)((k >> 24) & 0xFF));
                    tlv.Add((byte)((k >> 16) & 0xFF));
                    tlv.Add((byte)((k >> 8) & 0xFF));
                    tlv.Add((byte)(k & 0xFF));
                }

                var data = ConvertBits([.. tlv], 8, 5, true);
                if (data == null) return null;
                return Bech32Encode("nevent", data);
            }
            catch
            {
                return null;
            }
        }
    }
}
