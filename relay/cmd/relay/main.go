package main

import (
	"flag"
	"log"
	"os"
	"os/signal"
	"syscall"

	"edgestpan.dev/relay/internal/relay"
)

func main() {
	httpAddr := flag.String("http", "0.0.0.0:8787", "signaling listen address")
	udpAddr := flag.String("udp", "0.0.0.0:8788", "UDP relay listen address")
	flag.Parse()
	server, err := relay.Start(*httpAddr, *udpAddr)
	if err != nil {
		log.Fatal(err)
	}
	log.Printf("EdgeSpan relay %s http %s udp %d", relay.Version, server.HTTPAddress(), server.UDPPort())
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)
	<-stop
	server.Close()
}
