// Package webui 内嵌 Web 前端资源。
package webui

import "embed"

// FS 包含 web 目录下的静态资源。
//
//go:embed web
var FS embed.FS

// Root 是静态资源在 FS 中的根目录。
const Root = "web"
