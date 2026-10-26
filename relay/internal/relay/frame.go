package relay

import "fmt"

const (
	magic0 = 0xE5
	magic1 = 0x52
	opBind = 1
	opData = 2
	opDeliver = 3
)

func encode(op byte, token []byte, peerID string, payload []byte) ([]byte, error) {
	if len(token) != 16 {
		return nil, fmt.Errorf("token")
	}
	id := []byte(peerID)
	if len(id) < 1 || len(id) > 64 {
		return nil, fmt.Errorf("peer id")
	}
	if len(payload) > 60000 {
		return nil, fmt.Errorf("payload")
	}
	buf := make([]byte, 21+len(id)+len(payload))
	buf[0], buf[1], buf[2], buf[3] = magic0, magic1, 1, op
	copy(buf[4:20], token)
	buf[20] = byte(len(id))
	copy(buf[21:], id)
	copy(buf[21+len(id):], payload)
	return buf, nil
}

func parse(buf []byte) (op byte, token []byte, peerID string, payload []byte, ok bool) {
	if len(buf) < 21 || buf[0] != magic0 || buf[1] != magic1 || buf[2] != 1 {
		return 0, nil, "", nil, false
	}
	if buf[3] < 1 || buf[3] > 3 {
		return 0, nil, "", nil, false
	}
	idLen := int(buf[20])
	if idLen > 64 || len(buf) < 21+idLen {
		return 0, nil, "", nil, false
	}
	token = append([]byte(nil), buf[4:20]...)
	peerID = string(buf[21 : 21+idLen])
	payload = append([]byte(nil), buf[21+idLen:]...)
	return buf[3], token, peerID, payload, true
}
