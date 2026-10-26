FROM golang:1.22-alpine AS build
WORKDIR /src
COPY go.mod ./
COPY cmd ./cmd
COPY internal ./internal
RUN CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o /edgestpan-relay ./cmd/relay

FROM alpine:3.20
RUN adduser -D -H edgestpan
USER edgestpan
COPY --from=build /edgestpan-relay /edgestpan-relay
EXPOSE 8787/tcp 8788/udp
ENTRYPOINT ["/edgestpan-relay", "-http", "0.0.0.0:8787", "-udp", "0.0.0.0:8788"]
