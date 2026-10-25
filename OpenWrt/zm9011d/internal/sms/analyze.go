package sms

import (
	"regexp"
	"strings"
)

// Analysis 是从短信正文中提取出的结构化信息。
type Analysis struct {
	// Code 验证码（可能为空）。
	Code string
	// Tag 标签：密码 / 验证码。
	Tag string
	// ServiceName 短信签名中的服务名，如“深圳一流网络技术”。
	ServiceName string
}

var (
	codePattern    = regexp.MustCompile(`(验证码|校验码|确认码|动态码|随机密码|密码|口令|code|password)[^0-9]{0,12}([0-9]{4,8})`)
	servicePattern = regexp.MustCompile(`【([^】]{1,30})】`)
)

// Analyze 识别验证码、标签与短信签名服务名。
func Analyze(body string) Analysis {
	var a Analysis

	if m := codePattern.FindStringSubmatch(body); len(m) == 3 {
		a.Code = m[2]
		keyword := strings.ToLower(m[1])
		if strings.Contains(keyword, "密码") || strings.Contains(keyword, "口令") || strings.Contains(keyword, "password") {
			a.Tag = "密码"
		} else {
			a.Tag = "验证码"
		}
	}

	if m := servicePattern.FindStringSubmatch(body); len(m) == 2 {
		a.ServiceName = m[1]
	}

	return a
}
