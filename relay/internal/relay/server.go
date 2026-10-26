package relay

import (
	"bufio"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"sync"
	"time"
)

const Version = "0.9.3"

type peer struct {
	id    string
	token []byte
	name  string
	role  string
	conn  net.Conn
	udp   *net.UDPAddr
	send  chan []byte
}

type envelope struct {
	Op      string `json:"op"`
	Name    string `json:"name,omitempty"`
	Role    string `json:"role,omitempty"`
	PeerID  string `json:"peerId,omitempty"`
	Token   string `json:"token,omitempty"`
	UdpPort int    `json:"udpPort,omitempty"`
	To      string `json:"to,omitempty"`
	From    string `json:"from,omitempty"`
	Data    string `json:"data,omitempty"`
	Message string `json:"message,omitempty"`
	Peers   []peerInfo `json:"peers,omitempty"`
}

type peerInfo struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	Role string `json:"role"`
}

type Server struct {
	udpPort int
	udp     *net.UDPConn
	http    *http.Server
	mu      sync.Mutex
	peers   map[string]*peer
	byToken map[string]string
}

func Start(httpAddr, udpAddr string) (*Server, error) {
	udp, err := net.ListenUDP("udp", mustUDP(udpAddr))
	if err != nil {
		return nil, err
	}
	s := &Server{
		udpPort: udp.LocalAddr().(*net.UDPAddr).Port,
		udp:     udp,
		peers:   map[string]*peer{},
		byToken: map[string]string{},
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/health", s.health)
	mux.HandleFunc("/v1/signal", s.signal)
	mux.HandleFunc("/", s.status)
	ln, err := net.Listen("tcp", httpAddr)
	if err != nil {
		udp.Close()
		return nil, err
	}
	s.http = &http.Server{Handler: mux, ReadHeaderTimeout: 10 * time.Second}
	go s.readUDP()
	go s.http.Serve(ln)
	s.http.Addr = ln.Addr().String()
	return s, nil
}

func (s *Server) HTTPAddress() string { return s.http.Addr }
func (s *Server) UDPPort() int        { return s.udpPort }

func (s *Server) Close() {
	s.http.Close()
	s.udp.Close()
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, p := range s.peers {
		p.conn.Close()
	}
}

func (s *Server) health(w http.ResponseWriter, _ *http.Request) {
	s.mu.Lock()
	n := len(s.peers)
	s.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"ok":true,"service":"edgestpan-relay","version":"%s","peers":%d}`, Version, n)
}

func (s *Server) status(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/" {
		http.NotFound(w, r)
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	fmt.Fprintf(w, `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="refresh" content="3"><title>EdgeSpan relay</title>
<style>
body{margin:0;background:#101418;color:#E8EEF4;font:16px/1.5 "Segoe UI",sans-serif}
main{max-width:720px;margin:40px auto;padding:0 20px}
h1{color:#2FBF9B;font-weight:600}
.card{background:#1B222C;border-radius:14px;padding:16px 18px;margin:12px 0}
.muted{color:#8B98A8}
.gold{color:#E0A45A}
</style></head><body><main>
<h1>EdgeSpan relay</h1>
<p class="muted">Signaling and encrypted-payload fallback. The relay stores no keys and cannot read pointer or keyboard traffic.</p>
<div class="card"><div class="gold">Version %s</div><div>%d peer%s connected · UDP %d</div></div>`, Version, len(s.peers), plural(len(s.peers)), s.udpPort)
	if len(s.peers) == 0 {
		fmt.Fprintf(w, `<div class="card muted">No peers yet. A Windows host or Android client appears here after internet mode is turned on.</div>`)
	}
	for _, p := range s.peers {
		fmt.Fprintf(w, `<div class="card"><strong>%s</strong><div class="muted">%s · %s</div></div>`, html(p.name), html(p.role), html(p.id))
	}
	fmt.Fprintf(w, `</main></body></html>`)
}

func (s *Server) signal(w http.ResponseWriter, r *http.Request) {
	conn, rw, err := upgrade(w, r)
	if err != nil {
		return
	}
	defer conn.Close()
	op, payload, err := readMessage(rw.Reader)
	if err != nil || op != 1 {
		return
	}
	var hello envelope
	if json.Unmarshal(payload, &hello) != nil || hello.Op != "hello" || hello.Name == "" {
		writeJSON(rw, envelope{Op: "error", Message: "hello required"})
		return
	}
	id, token := newIdentity()
	p := &peer{id: id, token: token, name: trim(hello.Name, 80), role: trim(hello.Role, 16), conn: conn, send: make(chan []byte, 32)}
	s.mu.Lock()
	s.peers[id] = p
	s.byToken[hex.EncodeToString(token)] = id
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		delete(s.peers, id)
		delete(s.byToken, hex.EncodeToString(token))
		s.mu.Unlock()
	}()
	if err := writeJSON(rw, envelope{Op: "welcome", PeerID: id, Token: base64.StdEncoding.EncodeToString(token), UdpPort: s.udpPort}); err != nil {
		return
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		for msg := range p.send {
			if writeFrame(rw, 1, msg) != nil || rw.Flush() != nil {
				return
			}
		}
	}()
	for {
		op, payload, err = readMessage(rw.Reader)
		if err != nil || op == 0x8 {
			break
		}
		if op == 0x9 {
			writeFrame(rw, 0xA, payload)
			rw.Flush()
			continue
		}
		if op != 1 {
			continue
		}
		var msg envelope
		if json.Unmarshal(payload, &msg) != nil {
			continue
		}
		switch msg.Op {
		case "signal":
			s.forward(p, msg)
		case "list":
			s.replyList(p)
		default:
			p.trySend(mustJSON(envelope{Op: "error", Message: "unknown op"}))
		}
	}
	close(p.send)
	<-done
}

func (s *Server) forward(from *peer, msg envelope) {
	s.mu.Lock()
	dest := s.peers[msg.To]
	s.mu.Unlock()
	if dest == nil {
		from.trySend(mustJSON(envelope{Op: "error", Message: "peer not found"}))
		return
	}
	dest.trySend(mustJSON(envelope{Op: "signal", From: from.id, Data: msg.Data}))
}

func (s *Server) replyList(to *peer) {
	s.mu.Lock()
	defer s.mu.Unlock()
	list := make([]peerInfo, 0, len(s.peers))
	for _, p := range s.peers {
		if p.id == to.id {
			continue
		}
		list = append(list, peerInfo{ID: p.id, Name: p.name, Role: p.role})
	}
	to.trySend(mustJSON(envelope{Op: "peers", Peers: list}))
}

func (p *peer) trySend(b []byte) {
	select {
	case p.send <- b:
	default:
	}
}

func (s *Server) readUDP() {
	buf := make([]byte, 65535)
	for {
		n, addr, err := s.udp.ReadFromUDP(buf)
		if err != nil {
			return
		}
		if reply := stunBinding(buf[:n], addr); reply != nil {
			s.udp.WriteToUDP(reply, addr)
			continue
		}
		op, token, id, payload, ok := parse(buf[:n])
		if !ok {
			continue
		}
		s.mu.Lock()
		switch op {
		case opBind:
			if p := s.peers[id]; p != nil && same(p.token, token) {
				p.udp = addr
			}
		case opData:
			srcID, known := s.byToken[hex.EncodeToString(token)]
			src := s.peers[srcID]
			dest := s.peers[id]
			if known && src != nil && dest != nil && dest.udp != nil && same(src.token, token) {
				src.udp = addr
				frame, ferr := encode(opDeliver, make([]byte, 16), srcID, payload)
				if ferr == nil {
					s.udp.WriteToUDP(frame, dest.udp)
				}
			}
		}
		s.mu.Unlock()
	}
}

func writeJSON(w *bufio.ReadWriter, msg envelope) error {
	if err := writeFrame(w, 1, mustJSON(msg)); err != nil {
		return err
	}
	return w.Flush()
}

func mustJSON(v any) []byte {
	b, err := json.Marshal(v)
	if err != nil {
		return []byte(`{"op":"error","message":"encode"}`)
	}
	return b
}

func newIdentity() (string, []byte) {
	id := make([]byte, 8)
	token := make([]byte, 16)
	rand.Read(id)
	rand.Read(token)
	return hex.EncodeToString(id), token
}

func same(a, b []byte) bool {
	if len(a) != len(b) {
		return false
	}
	var d byte
	for i := range a {
		d |= a[i] ^ b[i]
	}
	return d == 0
}

func trim(s string, n int) string {
	if len(s) > n {
		return s[:n]
	}
	return s
}

func plural(n int) string {
	if n == 1 {
		return ""
	}
	return "s"
}

func html(s string) string {
	out := make([]byte, 0, len(s))
	for i := 0; i < len(s); i++ {
		switch s[i] {
		case '<':
			out = append(out, "&lt;"...)
		case '>':
			out = append(out, "&gt;"...)
		case '&':
			out = append(out, "&amp;"...)
		default:
			if s[i] >= 32 {
				out = append(out, s[i])
			}
		}
	}
	return string(out)
}

// stunBinding answers an RFC 5389 Binding request with an XOR-MAPPED-ADDRESS
// so a self-hosted relay can replace a public STUN service.
func stunBinding(pkt []byte, addr *net.UDPAddr) []byte {
	if len(pkt) < 20 || pkt[0] != 0x00 || pkt[1] != 0x01 {
		return nil
	}
	if pkt[4] != 0x21 || pkt[5] != 0x12 || pkt[6] != 0xA4 || pkt[7] != 0x42 {
		return nil
	}
	ip4 := addr.IP.To4()
	if ip4 == nil {
		return nil
	}
	port := uint16(addr.Port) ^ 0x2112
	xip := [4]byte{ip4[0] ^ 0x21, ip4[1] ^ 0x12, ip4[2] ^ 0xA4, ip4[3] ^ 0x42}
	out := make([]byte, 32)
	out[0], out[1] = 0x01, 0x01
	out[2], out[3] = 0x00, 12
	copy(out[4:20], pkt[4:20])
	out[20], out[21] = 0x00, 0x20
	out[22], out[23] = 0x00, 8
	out[24] = 0
	out[25] = 0x01
	out[26] = byte(port >> 8)
	out[27] = byte(port)
	copy(out[28:32], xip[:])
	return out
}

func mustUDP(addr string) *net.UDPAddr {
	u, err := net.ResolveUDPAddr("udp", addr)
	if err != nil {
		panic(err)
	}
	return u
}
