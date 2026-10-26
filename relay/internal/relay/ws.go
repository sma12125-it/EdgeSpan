package relay

import (
	"bufio"
	"crypto/sha1"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"net/http"
	"strings"
)

const wsGUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

func upgrade(w http.ResponseWriter, r *http.Request) (net.Conn, *bufio.ReadWriter, error) {
	if !strings.EqualFold(r.Header.Get("Upgrade"), "websocket") || !headerHas(r.Header.Get("Connection"), "upgrade") {
		http.Error(w, "websocket required", http.StatusBadRequest)
		return nil, nil, errors.New("upgrade")
	}
	key := r.Header.Get("Sec-WebSocket-Key")
	if key == "" {
		http.Error(w, "missing key", http.StatusBadRequest)
		return nil, nil, errors.New("key")
	}
	hj, ok := w.(http.Hijacker)
	if !ok {
		return nil, nil, errors.New("hijack")
	}
	conn, rw, err := hj.Hijack()
	if err != nil {
		return nil, nil, err
	}
	sum := sha1.Sum([]byte(key + wsGUID))
	accept := base64.StdEncoding.EncodeToString(sum[:])
	_, err = io.WriteString(rw, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+accept+"\r\n\r\n")
	if err != nil {
		conn.Close()
		return nil, nil, err
	}
	if err = rw.Flush(); err != nil {
		conn.Close()
		return nil, nil, err
	}
	return conn, rw, nil
}

func headerHas(value, token string) bool {
	for _, part := range strings.Split(value, ",") {
		if strings.EqualFold(strings.TrimSpace(part), token) {
			return true
		}
	}
	return false
}

func readMessage(r *bufio.Reader) (opcode byte, payload []byte, err error) {
	var acc []byte
	var started byte
	for {
		fin, op, chunk, rerr := readFrame(r)
		if rerr != nil {
			return 0, nil, rerr
		}
		if op == 0x8 {
			return 0x8, chunk, nil
		}
		if op == 0x9 || op == 0xA {
			return op, chunk, nil
		}
		if op != 0 {
			if started != 0 {
				return 0, nil, errors.New("unexpected frame")
			}
			started = op
		} else if started == 0 {
			return 0, nil, errors.New("continuation")
		}
		acc = append(acc, chunk...)
		if len(acc) > 1<<20 {
			return 0, nil, errors.New("message too large")
		}
		if fin {
			return started, acc, nil
		}
	}
}

func readFrame(r *bufio.Reader) (fin bool, opcode byte, payload []byte, err error) {
	h := make([]byte, 2)
	if _, err = io.ReadFull(r, h); err != nil {
		return
	}
	if h[0]&0x70 != 0 {
		err = errors.New("reserved bits")
		return
	}
	fin = h[0]&0x80 != 0
	opcode = h[0] & 0x0F
	masked := h[1]&0x80 != 0
	length := uint64(h[1] & 0x7F)
	switch length {
	case 126:
		ext := make([]byte, 2)
		if _, err = io.ReadFull(r, ext); err != nil {
			return
		}
		length = uint64(binary.BigEndian.Uint16(ext))
	case 127:
		ext := make([]byte, 8)
		if _, err = io.ReadFull(r, ext); err != nil {
			return
		}
		length = binary.BigEndian.Uint64(ext)
	}
	if length > 1<<20 {
		err = errors.New("frame too large")
		return
	}
	if !masked {
		err = errors.New("client frame is not masked")
		return
	}
	var mask [4]byte
	if _, err = io.ReadFull(r, mask[:]); err != nil {
		return
	}
	payload = make([]byte, length)
	if _, err = io.ReadFull(r, payload); err != nil {
		return
	}
	for i := range payload {
		payload[i] ^= mask[i%4]
	}
	return
}

func writeFrame(w io.Writer, opcode byte, payload []byte) error {
	n := len(payload)
	var hdr []byte
	switch {
	case n < 126:
		hdr = []byte{0x80 | opcode, byte(n)}
	case n <= 65535:
		hdr = []byte{0x80 | opcode, 126, byte(n >> 8), byte(n)}
	default:
		hdr = make([]byte, 10)
		hdr[0] = 0x80 | opcode
		hdr[1] = 127
		binary.BigEndian.PutUint64(hdr[2:], uint64(n))
	}
	if _, err := w.Write(hdr); err != nil {
		return err
	}
	_, err := w.Write(payload)
	return err
}
