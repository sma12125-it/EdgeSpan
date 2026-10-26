package relay

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"net"
	"net/http"
	"strings"
	"testing"
	"time"
)

func TestSignalAndUdp(t *testing.T) {
	s, err := Start("127.0.0.1:0", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	a := dial(t, s.HTTPAddress(), "Office", "host")
	b := dial(t, s.HTTPAddress(), "Tablet", "client")
	defer a.conn.Close()
	defer b.conn.Close()
	if err := writeClient(a.rw, text(`{"op":"signal","to":"`+b.id+`","data":"hello-tablet"}`)); err != nil {
		t.Fatal(err)
	}
	op, payload, err := readServer(b.rw.Reader)
	if err != nil || op != 1 {
		t.Fatal(err)
	}
	var got envelope
	if json.Unmarshal(payload, &got) != nil || got.Op != "signal" || got.From != a.id || got.Data != "hello-tablet" {
		t.Fatalf("signal %+v", got)
	}

	ua, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer ua.Close()
	ub, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer ub.Close()
	relayAddr := &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: s.UDPPort()}
	bindA, _ := encode(opBind, a.token, a.id, nil)
	bindB, _ := encode(opBind, b.token, b.id, nil)
	ua.WriteToUDP(bindA, relayAddr)
	ub.WriteToUDP(bindB, relayAddr)
	time.Sleep(50 * time.Millisecond)
	data, _ := encode(opData, a.token, b.id, []byte("cipher"))
	ua.WriteToUDP(data, relayAddr)
	ub.SetReadDeadline(time.Now().Add(time.Second))
	buf := make([]byte, 1500)
	n, _, err := ub.ReadFromUDP(buf)
	if err != nil {
		t.Fatal(err)
	}
	opb, _, peer, payload, ok := parse(buf[:n])
	if !ok || opb != opDeliver || peer != a.id || string(payload) != "cipher" {
		t.Fatalf("deliver op=%d peer=%s payload=%q", opb, peer, payload)
	}

	res, err := http.Get("http://" + s.HTTPAddress() + "/health")
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != 200 {
		t.Fatal(res.Status)
	}
}

type client struct {
	conn  net.Conn
	rw    *bufio.ReadWriter
	id    string
	token []byte
}

func dial(t *testing.T, addr, name, role string) client {
	t.Helper()
	conn, err := net.Dial("tcp", addr)
	if err != nil {
		t.Fatal(err)
	}
	rw := bufio.NewReadWriter(bufio.NewReader(conn), bufio.NewWriter(conn))
	key := "dGhlIHNhbXBsZSBub25jZQ=="
	ioWrite(t, rw, "GET /v1/signal HTTP/1.1\r\nHost: "+addr+"\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: "+key+"\r\nSec-WebSocket-Version: 13\r\n\r\n")
	status, err := rw.ReadString('\n')
	if err != nil || !strings.Contains(status, "101") {
		t.Fatal(status, err)
	}
	for {
		line, err := rw.ReadString('\n')
		if err != nil {
			t.Fatal(err)
		}
		if line == "\r\n" {
			break
		}
	}
	if err := writeClient(rw, text(`{"op":"hello","name":"`+name+`","role":"`+role+`"}`)); err != nil {
		t.Fatal(err)
	}
	op, payload, err := readServer(rw.Reader)
	if err != nil || op != 1 {
		t.Fatal(err)
	}
	var welcome envelope
	if json.Unmarshal(payload, &welcome) != nil || welcome.Op != "welcome" || welcome.PeerID == "" {
		t.Fatalf("welcome %s", payload)
	}
	token, err := base64.StdEncoding.DecodeString(welcome.Token)
	if err != nil || len(token) != 16 {
		t.Fatal(welcome.Token)
	}
	return client{conn: conn, rw: rw, id: welcome.PeerID, token: token}
}

func writeClient(w *bufio.ReadWriter, payload []byte) error {
	mask := []byte{1, 2, 3, 4}
	frame := []byte{0x81, byte(0x80 | len(payload))}
	frame = append(frame, mask...)
	for i, b := range payload {
		frame = append(frame, b^mask[i%4])
	}
	if _, err := w.Write(frame); err != nil {
		return err
	}
	return w.Flush()
}

func readServer(r *bufio.Reader) (opcode byte, payload []byte, err error) {
	h := make([]byte, 2)
	if _, err = ioReadFull(r, h); err != nil {
		return
	}
	opcode = h[0] & 0x0F
	length := int(h[1] & 0x7F)
	if length == 126 {
		ext := make([]byte, 2)
		if _, err = ioReadFull(r, ext); err != nil {
			return
		}
		length = int(ext[0])<<8 | int(ext[1])
	}
	payload = make([]byte, length)
	_, err = ioReadFull(r, payload)
	return
}

func ioReadFull(r *bufio.Reader, buf []byte) (int, error) {
	n := 0
	for n < len(buf) {
		got, err := r.Read(buf[n:])
		n += got
		if err != nil {
			return n, err
		}
	}
	return n, nil
}

func text(s string) []byte { return []byte(s) }

func ioWrite(t *testing.T, w *bufio.ReadWriter, s string) {
	t.Helper()
	if _, err := w.WriteString(s); err != nil {
		t.Fatal(err)
	}
	if err := w.Flush(); err != nil {
		t.Fatal(err)
	}
}
