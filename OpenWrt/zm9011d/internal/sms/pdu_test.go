package sms

import (
	"strings"
	"testing"
)

func TestDecodeDeliverUCS2(t *testing.T) {
	pdu := "07917283010010F5040D91683108108300F0000862608261515440044F60597D"
	msg, err := DecodeDeliver(pdu)
	if err != nil {
		t.Fatalf("decode failed: %v", err)
	}
	if msg.Sender != "+8613800138000" {
		t.Fatalf("sender=%q", msg.Sender)
	}
	if msg.Body != "你好" {
		t.Fatalf("body=%q", msg.Body)
	}
	if !msg.HasTimestamp || msg.Timestamp.Format("2006-01-02 15:04:05") != "2026-06-28 16:15:45" {
		t.Fatalf("timestamp=%v", msg.Timestamp)
	}
}

func TestDecodeDeliverGSM7(t *testing.T) {
	pdu := "07917283010010F5040D91683108108300F000006260826151544005E8329BFD06"
	msg, err := DecodeDeliver(pdu)
	if err != nil {
		t.Fatalf("decode failed: %v", err)
	}
	if msg.Sender != "+8613800138000" {
		t.Fatalf("sender=%q", msg.Sender)
	}
	if msg.Body != "hello" {
		t.Fatalf("body=%q", msg.Body)
	}
}

func TestUCS2RoundTrip(t *testing.T) {
	text := "随机密码：06692310"
	hexText := ToUCS2Hex(text)
	if got := DecodeUCS2Hex(hexText); got != text {
		t.Fatalf("round trip=%q", got)
	}
}

func TestAnalyze(t *testing.T) {
	body := "【深圳一流网络技术】恭喜您成功注册成为一牛网会员，随机密码：06692310，请登录后修改密码！"
	a := Analyze(body)
	if a.Code != "06692310" {
		t.Fatalf("code=%q", a.Code)
	}
	if a.Tag != "密码" {
		t.Fatalf("tag=%q", a.Tag)
	}
	if a.ServiceName != "深圳一流网络技术" {
		t.Fatalf("service=%q", a.ServiceName)
	}
	if !strings.Contains(body, a.Code) {
		t.Fatalf("code not in body")
	}
}
