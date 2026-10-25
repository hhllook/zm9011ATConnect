// Package api 提供 HTTP JSON API、SSE 实时推送与静态页面服务。
package api

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"strings"
	"time"

	"zm9011d/internal/at"
	"zm9011d/internal/config"
	"zm9011d/internal/logbuf"
	"zm9011d/internal/sms"
	"zm9011d/internal/store"
	"zm9011d/internal/webui"
)

// Server 是 HTTP 服务。
type Server struct {
	cfg     config.Config
	at      *at.Client
	sms     *sms.Service
	store   *store.Store
	logs    *logbuf.Buffer
	version string
}

// New 创建 HTTP 服务。
func New(cfg config.Config, client *at.Client, smsSvc *sms.Service, st *store.Store, logs *logbuf.Buffer, version string) *Server {
	return &Server{
		cfg:     cfg,
		at:      client,
		sms:     smsSvc,
		store:   st,
		logs:    logs,
		version: version,
	}
}

// Start 启动 HTTP 服务，ctx 取消时优雅退出。
func (s *Server) Start(ctx context.Context) {
	mux := http.NewServeMux()

	mux.HandleFunc("GET /api/state", s.handleState)
	mux.HandleFunc("GET /api/messages", s.handleMessages)
	mux.HandleFunc("POST /api/messages/read", s.handleMarkRead)
	mux.HandleFunc("POST /api/messages/delete", s.handleDelete)
	mux.HandleFunc("POST /api/messages/clear", s.handleClear)
	mux.HandleFunc("POST /api/sms/send", s.handleSend)
	mux.HandleFunc("GET /api/device", s.handleDevice)
	mux.HandleFunc("GET /api/network", s.handleNetwork)
	mux.HandleFunc("GET /api/logs", s.handleLogs)
	mux.HandleFunc("GET /api/events", s.handleEvents)
	mux.HandleFunc("POST /api/at", s.handleAt)
	mux.HandleFunc("GET /api/config", s.handleConfig)

	static, err := fs.Sub(webui.FS, webui.Root)
	if err != nil {
		s.logs.Error("[api.Start] 加载内嵌前端资源失败 error=%v", err)
		return
	}
	mux.Handle("/", http.FileServer(http.FS(static)))

	handler := s.withAuth(mux)
	srv := &http.Server{
		Addr:              s.cfg.Addr(),
		Handler:           handler,
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		s.logs.Info("[api.Start] Web 服务已启动 http://%s", s.cfg.Addr())
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			s.logs.Error("[api.Start] Web 服务异常退出 addr=%s error=%v", s.cfg.Addr(), err)
		}
	}()

	<-ctx.Done()

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	_ = srv.Shutdown(shutdownCtx)
	s.logs.Info("[api.Start] Web 服务已停止")
}

func (s *Server) withAuth(next http.Handler) http.Handler {
	if s.cfg.Password == "" {
		return next
	}

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, password, ok := r.BasicAuth()
		if !ok || subtle.ConstantTimeCompare([]byte(password), []byte(s.cfg.Password)) != 1 {
			w.Header().Set("WWW-Authenticate", `Basic realm="ZM9011", charset="UTF-8"`)
			http.Error(w, "Unauthorized", http.StatusUnauthorized)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (s *Server) handleState(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{
		"serial":          s.at.Status(),
		"deleteAfterRead": s.cfg.DeleteAfterRead,
		"messages":        s.store.Count(),
		"version":         s.version,
		"addr":            s.cfg.Addr(),
	})
}

func (s *Server) handleMessages(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, s.sms.List())
}

type idsRequest struct {
	IDs []string `json:"ids"`
}

func (s *Server) handleMarkRead(w http.ResponseWriter, r *http.Request) {
	var req idsRequest
	if err := decodeJSON(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, err.Error())
		return
	}
	if err := s.sms.MarkRead(req.IDs); err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (s *Server) handleDelete(w http.ResponseWriter, r *http.Request) {
	var req idsRequest
	if err := decodeJSON(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, err.Error())
		return
	}
	if err := s.sms.Delete(req.IDs); err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (s *Server) handleClear(w http.ResponseWriter, r *http.Request) {
	if err := s.sms.Clear(); err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

type sendRequest struct {
	Sender string `json:"sender"`
	Text   string `json:"text"`
}

func (s *Server) handleSend(w http.ResponseWriter, r *http.Request) {
	var req sendRequest
	if err := decodeJSON(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, err.Error())
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 40*time.Second)
	defer cancel()

	msg, err := s.sms.Send(ctx, req.Sender, req.Text)
	if err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, msg)
}

func (s *Server) handleDevice(w http.ResponseWriter, r *http.Request) {
	queries := []query{
		{Name: "产品信息", Command: "ATI"},
		{Name: "软件版本", Command: "AT+CGMR"},
		{Name: "设备型号", Command: "AT+CGMM"},
		{Name: "IMEI", Command: "AT+CGSN"},
		{Name: "IMSI", Command: "AT+CIMI"},
		{Name: "SIM 状态", Command: "AT+CPIN?"},
		{Name: "信号质量", Command: "AT+CSQ"},
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": s.runQueries(r.Context(), queries)})
}

func (s *Server) handleNetwork(w http.ResponseWriter, r *http.Request) {
	queries := []query{
		{Name: "网络注册", Command: "AT+CREG?"},
		{Name: "运营商", Command: "AT+COPS?"},
		{Name: "信号质量", Command: "AT+CSQ"},
		{Name: "信号强度", Command: "AT+ZRSSI?"},
		{Name: "小区信息", Command: "AT+ZCELLINFO?"},
		{Name: "注册域", Command: "AT+ZPAS?"},
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": s.runQueries(r.Context(), queries)})
}

type query struct {
	Name    string
	Command string
}

type queryResult struct {
	Name    string `json:"name"`
	Command string `json:"command"`
	OK      bool   `json:"ok"`
	Output  string `json:"output"`
	Error   string `json:"error,omitempty"`
}

func (s *Server) runQueries(parent context.Context, queries []query) []queryResult {
	results := make([]queryResult, 0, len(queries))
	for _, q := range queries {
		ctx, cancel := context.WithTimeout(parent, 4*time.Second)
		res, err := s.at.Send(ctx, q.Command, 3*time.Second)
		cancel()

		output := strings.Join(res.Lines, "\n")
		if strings.TrimSpace(output) == "" && res.Final != "" {
			output = res.Final
		}

		item := queryResult{
			Name:    q.Name,
			Command: q.Command,
			OK:      err == nil && res.Success,
			Output:  output,
		}
		if err != nil {
			item.Error = err.Error()
		} else if !res.Success {
			if res.Final != "" {
				item.Error = res.Final
			} else {
				item.Error = "命令失败"
			}
		}

		results = append(results, item)
	}
	return results
}

func (s *Server) handleLogs(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, s.logs.Snapshot())
}

type atRequest struct {
	Command string `json:"command"`
}

func (s *Server) handleAt(w http.ResponseWriter, r *http.Request) {
	var req atRequest
	if err := decodeJSON(r, &req); err != nil {
		writeError(w, http.StatusBadRequest, err.Error())
		return
	}
	req.Command = strings.TrimSpace(req.Command)
	if req.Command == "" || len(req.Command) > 512 {
		writeError(w, http.StatusBadRequest, "命令为空或过长")
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()

	res, err := s.at.Send(ctx, req.Command, 10*time.Second)
	if err != nil {
		writeJSON(w, http.StatusOK, res)
		return
	}
	writeJSON(w, http.StatusOK, res)
}

func (s *Server) handleConfig(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{
		"device":          s.cfg.Device,
		"baud":            s.cfg.Baud,
		"listen":          s.cfg.Listen,
		"port":            s.cfg.Port,
		"deleteAfterRead": s.cfg.DeleteAfterRead,
		"version":         s.version,
	})
}

func (s *Server) handleEvents(w http.ResponseWriter, r *http.Request) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		writeError(w, http.StatusInternalServerError, "当前服务器不支持流式响应")
		return
	}

	w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.Header().Set("X-Accel-Buffering", "no")

	logCh, cancelLog := s.logs.Subscribe()
	defer cancelLog()
	smsCh, cancelSMS := s.sms.Subscribe()
	defer cancelSMS()

	writeSSE(w, flusher, "ready", map[string]string{"status": "ok"})

	ticker := time.NewTicker(20 * time.Second)
	defer ticker.Stop()

	for {
		select {
		case <-r.Context().Done():
			return
		case entry, ok := <-logCh:
			if !ok {
				return
			}
			writeSSE(w, flusher, "log", entry)
		case msg, ok := <-smsCh:
			if !ok {
				return
			}
			writeSSE(w, flusher, "sms", msg)
		case <-ticker.C:
			_, _ = fmt.Fprint(w, ": ping\n\n")
			flusher.Flush()
		}
	}
}

func writeSSE(w http.ResponseWriter, flusher http.Flusher, event string, data any) {
	payload, err := json.Marshal(data)
	if err != nil {
		return
	}
	_, _ = fmt.Fprintf(w, "event: %s\ndata: %s\n\n", event, payload)
	flusher.Flush()
}

func decodeJSON(r *http.Request, dst any) error {
	body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20))
	if err != nil {
		return err
	}
	if len(body) == 0 {
		return errors.New("请求体为空")
	}
	return json.Unmarshal(body, dst)
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func writeError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}
