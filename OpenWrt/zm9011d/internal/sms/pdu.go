// Package sms 负责 GSM 03.40 短信 PDU 解码与验证码识别。
package sms

import (
	"encoding/binary"
	"encoding/hex"
	"errors"
	"strings"
	"time"
	"unicode/utf16"
)

// Decoded 是一条 SMS-DELIVER PDU 的解码结果。
type Decoded struct {
	Sender       string
	Body         string
	Timestamp    time.Time
	HasTimestamp bool
	RawPDU       string
}

// gsm7Basic 是 GSM 03.38 基本字符表（索引 0-127）。
var gsm7Basic = func() []rune {
	s := []rune("@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\x1bÆæßÉ")
	s = append(s, []rune(" !\"#¤%&'()*+,-./")...)
	for r := '0'; r <= '9'; r++ {
		s = append(s, r)
	}
	s = append(s, []rune(":;<=>?¡")...)
	for r := 'A'; r <= 'Z'; r++ {
		s = append(s, r)
	}
	s = append(s, []rune("ÄÖÑÜ§¿")...)
	for r := 'a'; r <= 'z'; r++ {
		s = append(s, r)
	}
	s = append(s, []rune("äöñüà")...)
	return s
}()

// gsm7Extension 是 GSM 7bit 扩展字符（0x1B 后的第二字节）。
var gsm7Extension = map[byte]rune{
	0x0A: '\f',
	0x14: '^',
	0x28: '{',
	0x29: '}',
	0x2F: '\\',
	0x3C: '[',
	0x3D: '~',
	0x3E: ']',
	0x40: '|',
	0x65: '€',
}

// DecodeDeliver 解码一条 SMS-DELIVER PDU（AT+CMGR 返回的十六进制串）。
func DecodeDeliver(pduHex string) (*Decoded, error) {
	clean := normalizeHex(pduHex)
	if len(clean) < 4 || len(clean)%2 != 0 {
		return nil, errors.New("pdu 长度非法")
	}

	raw, err := hex.DecodeString(clean)
	if err != nil {
		return nil, err
	}

	pos := 0
	if pos >= len(raw) {
		return nil, errors.New("pdu 内容为空")
	}

	// SMSC：1 字节长度 + 地址内容
	smscLen := int(raw[pos])
	pos++
	pos += smscLen
	if pos >= len(raw) {
		return nil, errors.New("pdu 缺少 tpdu")
	}

	// 首字节：TP-MTI 低 2 位，0 表示 SMS-DELIVER
	firstOctet := raw[pos]
	pos++
	if firstOctet&0x03 != 0 {
		return nil, errors.New("不是 SMS-DELIVER")
	}

	// TP-OA：地址位数、号码类型、半字节交换的号码
	if pos+2 > len(raw) {
		return nil, errors.New("pdu 缺少发送方地址")
	}
	addrDigits := int(raw[pos])
	pos++
	toa := raw[pos]
	pos++
	addrOctets := (addrDigits + 1) / 2
	if pos+addrOctets > len(raw) {
		return nil, errors.New("pdu 发送方地址不完整")
	}
	sender := decodeAddress(raw[pos:pos+addrOctets], addrDigits)
	pos += addrOctets
	if (toa>>4)&0x07 == 1 && !strings.HasPrefix(sender, "+") {
		sender = "+" + sender
	}

	// TP-PID / TP-DCS
	if pos+2 > len(raw) {
		return nil, errors.New("pdu 缺少 pid/dcs")
	}
	pos++ // PID
	dcs := raw[pos]
	pos++

	// TP-SCTS：7 字节时间戳
	var timestamp time.Time
	hasTimestamp := false
	if pos+7 <= len(raw) {
		timestamp, hasTimestamp = decodeTimestamp(raw[pos : pos+7])
		pos += 7
	}

	// TP-UDL / TP-UD
	if pos >= len(raw) {
		return nil, errors.New("pdu 缺少用户数据")
	}
	udl := int(raw[pos])
	pos++

	var body string
	switch decodeAlphabet(dcs) {
	case 2:
		body = decodeUCS2(raw[pos:], udl)
	case 0:
		body = decodeGSM7(raw[pos:], udl)
	default:
		body = decode8Bit(raw[pos:], udl)
	}

	return &Decoded{
		Sender:       sender,
		Body:         strings.TrimRight(body, "\x00"),
		Timestamp:    timestamp,
		HasTimestamp: hasTimestamp,
		RawPDU:       clean,
	}, nil
}

// DecodeUCS2Hex 把 UCS2 HEX 文本转换为普通字符串。
func DecodeUCS2Hex(value string) string {
	clean := normalizeHex(value)
	if len(clean) < 4 || len(clean)%4 != 0 {
		return value
	}
	b, err := hex.DecodeString(clean)
	if err != nil {
		return value
	}
	pairs := make([]uint16, 0, len(b)/2)
	for i := 0; i+1 < len(b); i += 2 {
		pairs = append(pairs, binary.BigEndian.Uint16(b[i:i+2]))
	}
	return string(utf16.Decode(pairs))
}

// ToUCS2Hex 把文本转换为 UCS2 HEX（发送中文短信使用）。
func ToUCS2Hex(text string) string {
	runes := []rune(text)
	buf := make([]byte, 0, len(runes)*2)
	for _, r := range runes {
		if r > 0xFFFF {
			r = '?'
		}
		buf = append(buf, byte(r>>8), byte(r))
	}
	return strings.ToUpper(hex.EncodeToString(buf))
}

func normalizeHex(value string) string {
	var sb strings.Builder
	for _, ch := range value {
		switch {
		case ch >= '0' && ch <= '9', ch >= 'a' && ch <= 'f', ch >= 'A' && ch <= 'F':
			sb.WriteRune(ch)
		}
	}
	return strings.ToUpper(sb.String())
}

func decodeAlphabet(dcs byte) int {
	switch {
	case dcs&0xC0 == 0x00:
		return int((dcs >> 2) & 0x03)
	case dcs&0xF0 == 0xF0:
		if dcs&0x04 != 0 {
			return 1
		}
		return 0
	case dcs&0xC0 == 0xC0:
		return 0
	default:
		return 0
	}
}

func decodeAddress(b []byte, digits int) string {
	var sb strings.Builder
	for _, v := range b {
		appendSemiOctet(&sb, v&0x0F, digits)
		appendSemiOctet(&sb, (v>>4)&0x0F, digits)
		if sb.Len() >= digits {
			break
		}
	}
	return sb.String()
}

func appendSemiOctet(sb *strings.Builder, v byte, max int) {
	if sb.Len() >= max || v == 0x0F {
		return
	}
	switch {
	case v <= 9:
		sb.WriteByte('0' + v)
	case v == 0x0A:
		sb.WriteByte('*')
	case v == 0x0B:
		sb.WriteByte('#')
	case v == 0x0C:
		sb.WriteByte('a')
	case v == 0x0D:
		sb.WriteByte('b')
	case v == 0x0E:
		sb.WriteByte('c')
	}
}

func decodeTimestamp(b []byte) (time.Time, bool) {
	if len(b) < 7 {
		return time.Time{}, false
	}
	year := semiOctet(b[0])
	month := semiOctet(b[1])
	day := semiOctet(b[2])
	hour := semiOctet(b[3])
	minute := semiOctet(b[4])
	second := semiOctet(b[5])
	t := time.Date(2000+year, time.Month(month), day, hour, minute, second, 0, time.Local)
	if t.Year() < 2000 || t.Month() < 1 || t.Month() > 12 {
		return time.Time{}, false
	}
	return t, true
}

func semiOctet(v byte) int {
	return int(v&0x0F)*10 + int((v>>4)&0x0F)
}

func decodeUCS2(b []byte, udl int) string {
	n := udl
	if n > len(b) {
		n = len(b)
	}
	if n%2 != 0 {
		n--
	}
	pairs := make([]uint16, 0, n/2)
	for i := 0; i+1 < n; i += 2 {
		pairs = append(pairs, binary.BigEndian.Uint16(b[i:i+2]))
	}
	return string(utf16.Decode(pairs))
}

func decode8Bit(b []byte, udl int) string {
	n := udl
	if n > len(b) {
		n = len(b)
	}
	runes := make([]rune, 0, n)
	for _, v := range b[:n] {
		runes = append(runes, rune(v))
	}
	return string(runes)
}

func decodeGSM7(b []byte, septets int) string {
	var sb strings.Builder
	escaped := false
	for i := 0; i < septets; i++ {
		bitOffset := i * 7
		byteIndex := bitOffset / 8
		bitIndex := bitOffset % 8
		if byteIndex >= len(b) {
			break
		}

		value := int(b[byteIndex]) >> bitIndex
		if bitIndex > 1 && byteIndex+1 < len(b) {
			value |= int(b[byteIndex+1]) << (8 - bitIndex)
		}
		value &= 0x7F

		if escaped {
			if r, ok := gsm7Extension[byte(value)]; ok {
				sb.WriteRune(r)
			} else {
				sb.WriteByte(' ')
			}
			escaped = false
			continue
		}
		if value == 0x1B {
			escaped = true
			continue
		}
		if value < len(gsm7Basic) {
			sb.WriteRune(gsm7Basic[value])
		}
	}
	return sb.String()
}
