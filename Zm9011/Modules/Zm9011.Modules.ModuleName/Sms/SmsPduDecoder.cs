using System;
using System.Collections.Generic;
using System.Text;

namespace Zm9011.Modules.ModuleName.Sms
{
    /// <summary>PDU 解码结果。</summary>
    public sealed class DecodedSms
    {
        /// <summary>发送方号码（国际号码带 + 前缀）。</summary>
        public string Sender { get; set; } = string.Empty;

        /// <summary>短信正文。</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>短信中心时间（解析失败时为 null）。</summary>
        public DateTime? Timestamp { get; set; }

        /// <summary>原始 PDU（十六进制）。</summary>
        public string RawPdu { get; set; } = string.Empty;
    }

    /// <summary>
    /// GSM 03.40 SMS-DELIVER PDU 解码器。
    /// 实时短信读取统一使用 PDU 模式，避免串口按 ASCII 解码导致中文乱码。
    /// </summary>
    public static class SmsPduDecoder
    {
        /// <summary>GSM 7bit 基本字符表（索引 0-127）。</summary>
        private const string Gsm7Basic =
            "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001BÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡" +
            "ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

        /// <summary>GSM 7bit 扩展字符（0x1B 后的第二字节）。</summary>
        private static readonly Dictionary<int, char> Gsm7Extension = new Dictionary<int, char>
        {
            { 0x0A, '\f' },
            { 0x14, '^' },
            { 0x28, '{' },
            { 0x29, '}' },
            { 0x2F, '\\' },
            { 0x3C, '[' },
            { 0x3D, '~' },
            { 0x3E, ']' },
            { 0x40, '|' },
            { 0x65, '€' }
        };

        /// <summary>
        /// 解码一条 SMS-DELIVER PDU（AT+CMGR 返回的十六进制串）。
        /// 非 DELIVER 类型、数据不完整或格式错误时返回 null。
        /// </summary>
        public static DecodedSms? DecodeDeliver(string pduHex)
        {
            if (string.IsNullOrWhiteSpace(pduHex))
            {
                return null;
            }

            var hex = NormalizeHex(pduHex);
            if (hex.Length < 4 || hex.Length % 2 != 0)
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                return null;
            }

            var pos = 0;

            // SMSC 地址：1 字节长度 + 地址内容（可跳过）
            if (pos >= bytes.Length)
            {
                return null;
            }
            var smscLength = bytes[pos++];
            pos += smscLength;
            if (pos >= bytes.Length)
            {
                return null;
            }

            // 首字节：TP-MTI 低 2 位，0 表示 SMS-DELIVER
            var firstOctet = bytes[pos++];
            if ((firstOctet & 0x03) != 0)
            {
                return null;
            }

            // TP-OA：地址长度（十进制位数）、号码类型、半字节交换的号码
            if (pos + 2 > bytes.Length)
            {
                return null;
            }
            var addressDigits = bytes[pos++];
            var typeOfAddress = bytes[pos++];
            var addressOctets = (addressDigits + 1) / 2;
            if (pos + addressOctets > bytes.Length)
            {
                return null;
            }
            var sender = DecodeAddress(bytes, pos, addressOctets, addressDigits);
            pos += addressOctets;
            if (((typeOfAddress >> 4) & 0x07) == 1 && !sender.StartsWith("+"))
            {
                sender = "+" + sender;
            }

            // TP-PID / TP-DCS
            if (pos + 2 > bytes.Length)
            {
                return null;
            }
            pos++; // PID
            var dcs = bytes[pos++];

            // TP-SCTS：7 字节时间戳
            DateTime? timestamp = null;
            if (pos + 7 <= bytes.Length)
            {
                timestamp = DecodeTimestamp(bytes, pos);
                pos += 7;
            }

            // TP-UDL / TP-UD
            if (pos >= bytes.Length)
            {
                return null;
            }
            var userDataLength = bytes[pos++];

            var alphabet = DecodeAlphabet(dcs);
            var available = bytes.Length - pos;
            string body;
            switch (alphabet)
            {
                case 0:
                    body = DecodeGsm7(bytes, pos, userDataLength, available);
                    break;
                case 2:
                    var ucs2Bytes = Math.Min(available, userDataLength);
                    body = Encoding.BigEndianUnicode.GetString(bytes, pos, ucs2Bytes);
                    break;
                default:
                    body = Encoding.Latin1.GetString(bytes, pos, Math.Min(available, userDataLength));
                    break;
            }

            return new DecodedSms
            {
                Sender = sender,
                Body = body.TrimEnd('\0'),
                Timestamp = timestamp,
                RawPdu = hex
            };
        }

        /// <summary>去空白并统一大写。</summary>
        private static string NormalizeHex(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (Uri.IsHexDigit(ch))
                {
                    sb.Append(char.ToUpperInvariant(ch));
                }
            }
            return sb.ToString();
        }

        /// <summary>解码 DCS 中的字符集：0 GSM7 / 1 8bit / 2 UCS2。</summary>
        private static int DecodeAlphabet(int dcs)
        {
            if ((dcs & 0xC0) == 0x00)
            {
                return (dcs >> 2) & 0x03;
            }
            if ((dcs & 0xF0) == 0xF0)
            {
                return (dcs & 0x04) != 0 ? 1 : 0;
            }
            if ((dcs & 0xC0) == 0xC0)
            {
                return 0;
            }
            return 0;
        }

        /// <summary>半字节交换的地址解码（0-9、*、#、a-c）。</summary>
        private static string DecodeAddress(byte[] bytes, int start, int octets, int digits)
        {
            var sb = new StringBuilder(digits);
            for (var i = 0; i < octets && sb.Length < digits; i++)
            {
                var b = bytes[start + i];
                AppendSemiOctet(sb, b & 0x0F, digits);
                AppendSemiOctet(sb, (b >> 4) & 0x0F, digits);
            }
            return sb.ToString();
        }

        private static void AppendSemiOctet(StringBuilder sb, int value, int maxDigits)
        {
            if (sb.Length >= maxDigits || value == 0x0F)
            {
                return;
            }
            sb.Append(value switch
            {
                <= 9 => (char)('0' + value),
                0x0A => '*',
                0x0B => '#',
                0x0C => 'a',
                0x0D => 'b',
                0x0E => 'c',
                _ => ' '
            });
        }

        /// <summary>解码 TP-SCTS（半字节交换的 YYMMDDHHmmSS + 时区）。</summary>
        private static DateTime? DecodeTimestamp(byte[] bytes, int start)
        {
            try
            {
                var year = SemiOctet(bytes[start]);
                var month = SemiOctet(bytes[start + 1]);
                var day = SemiOctet(bytes[start + 2]);
                var hour = SemiOctet(bytes[start + 3]);
                var minute = SemiOctet(bytes[start + 4]);
                var second = SemiOctet(bytes[start + 5]);
                return new DateTime(2000 + year, month, day, hour, minute, second);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static int SemiOctet(byte value) => (value & 0x0F) * 10 + ((value >> 4) & 0x0F);

        /// <summary>解包 GSM 7bit 七位编码，并处理扩展字符。</summary>
        private static string DecodeGsm7(byte[] bytes, int start, int septetCount, int available)
        {
            var sb = new StringBuilder(septetCount);
            var escaped = false;
            for (var i = 0; i < septetCount; i++)
            {
                var bitOffset = i * 7;
                var byteIndex = bitOffset / 8;
                var bitIndex = bitOffset % 8;
                if (byteIndex >= available)
                {
                    break;
                }

                var value = bytes[start + byteIndex] >> bitIndex;
                if (bitIndex > 1 && byteIndex + 1 < available)
                {
                    value |= bytes[start + byteIndex + 1] << (8 - bitIndex);
                }
                value &= 0x7F;

                if (escaped)
                {
                    sb.Append(Gsm7Extension.TryGetValue(value, out var ext) ? ext : ' ');
                    escaped = false;
                }
                else if (value == 0x1B)
                {
                    escaped = true;
                }
                else if (value < Gsm7Basic.Length)
                {
                    sb.Append(Gsm7Basic[value]);
                }
            }
            return sb.ToString();
        }
    }
}
