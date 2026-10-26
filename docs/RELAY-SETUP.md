# EdgeSpan relay

The relay lets a PC and a phone pair when they are not on the same Wi-Fi. One small VPS is enough. The apps talk to it over HTTP on port 8787 and UDP on port 8788.

## English

1. Rent a small Linux VPS. Ubuntu 22.04 or 24.04 is fine. 1 GB of RAM is enough.
2. Install Docker and the Docker Compose plugin.
3. Copy the `relay` folder from this project onto the server.
4. From that folder, start the relay:

```bash
docker compose up -d --build
```

5. Open these ports on the VPS firewall and on the cloud security group:

- TCP **8787** (signaling)
- UDP **8788** (the encrypted session)

Example with `ufw`:

```bash
sudo ufw allow 8787/tcp
sudo ufw allow 8788/udp
```

6. In the EdgeSpan apps, set the relay address to:

```text
http://YOUR_SERVER_IP:8787
```

There is no trailing slash.

### Where to type the address

- **Windows:** open **Connect**, choose **Internet**, and type the address in the relay field. You can also save it under **Network**.
- **Android:** open **Connect**, choose **Internet**, and type the address. The same field is under **Settings**.

Then choose Internet on the computer so it shows a PIN, and enter that PIN on the phone. The computer remembers this method and will try it again next launch.

### Run the binary

Docker is optional. The `relay` folder builds a single program.

Linux:

```bash
cd relay
go build -ldflags "-s -w" -o edgestpan-relay ./cmd/relay
./edgestpan-relay
```

Windows, from the same folder on a machine with Go, or cross-compiled:

```bash
GOOS=windows GOARCH=amd64 go build -ldflags "-s -w" -o edgestpan-relay.exe ./cmd/relay
```

Run `edgestpan-relay.exe`. It listens on TCP **8787** and UDP **8788**. Open both ports. In both apps the relay address is `http://YOUR_SERVER_IP:8787`.

### How the apps connect

1. Each side asks the relay's own UDP port for its public address. That request is STUN. Leave the STUN field empty and EdgeSpan uses the relay. Do not point it at a Google STUN server.
2. The two sides exchange those addresses through the relay, still inside the PIN or QR pairing.
3. They try a direct UDP connection first.
4. If that does not open within a few seconds, the encrypted session stays on the relay. The same PIN and the same encryption are used either way.

### A PC with a public address

If the computer already has a public IP, or the router forwards a UDP port to it, you can skip the relay.

- **Windows:** on the Internet page, fill in **Public address for port forwarding**. Port `0` means the port EdgeSpan is already listening on. The QR code includes that address.
- **Android:** on the Internet page, type `host:port` in **PC public address**.

The phone tries that address before hole punching and before the relay.

### Optional domain and TLS

A domain is optional. Point an A record at the VPS, then put Caddy or nginx in front of port 8787 only. The WebSocket upgrades on that same port. UDP 8788 stays plain UDP; do not put it behind HTTPS.

Caddyfile example:

```text
relay.example.com {
    reverse_proxy 127.0.0.1:8787
}
```

Change the published HTTP port in `docker-compose.yml` to `127.0.0.1:8787:8787` so only Caddy can reach it, and keep `8788:8788/udp` public.

Use this address in both apps:

```text
https://relay.example.com
```

## فارسی

رله برای وقتی است که رایانه و گوشی روی یک وای‌فای نیستند. یک سرور ارزان کافی است. برنامه‌ها با HTTP روی درگاه ۸۷۸۷ و با UDP روی درگاه ۸۷۸۸ به آن وصل می‌شوند.

1. یک سرور لینوکس کوچک بگیرید. اوبونتو ۲۲.۰۴ یا ۲۴.۰۴ مناسب است. یک گیگابایت رم بس است.
2. Docker و افزونه Docker Compose را نصب کنید.
3. پوشه `relay` همین پروژه را روی سرور بگذارید.
4. داخل همان پوشه رله را اجرا کنید:

```bash
docker compose up -d --build
```

5. این درگاه‌ها را در فایروال سرور و در گروه امنیتی ابر باز کنید:

- TCP **8787** برای هماهنگی
- UDP **8788** برای نشست رمزشده

نمونه با `ufw`:

```bash
sudo ufw allow 8787/tcp
sudo ufw allow 8788/udp
```

6. در هر دو برنامه اج‌اسپن این نشانی را بنویسید:

```text
http://YOUR_SERVER_IP:8787
```

اسلش آخر نمی‌خواهد.

### نشانی را کجا وارد کنید

- **ویندوز:** بروید به **اتصال**، **اینترنت** را انتخاب کنید و نشانی را در کادر رله بنویسید. از صفحه **شبکه** هم می‌شود ذخیره کرد.
- **اندروید:** بروید به **اتصال**، **اینترنت** را انتخاب کنید و نشانی را بنویسید. همان کادر در **تنظیمات** هم هست.

بعد روی رایانه اینترنت را انتخاب کنید تا رمز را نشان دهد و همان رمز را روی گوشی وارد کنید. رایانه این روش را به خاطر می‌سپارد و دفعه بعد همان را امتحان می‌کند.

### اجرای فایل آماده

داکر لازم نیست. از پوشه `relay` یک برنامه ساخته می‌شود.

لینوکس:

```bash
cd relay
go build -ldflags "-s -w" -o edgestpan-relay ./cmd/relay
./edgestpan-relay
```

ویندوز، روی همان پوشه یا با ساخت از راه دور:

```bash
GOOS=windows GOARCH=amd64 go build -ldflags "-s -w" -o edgestpan-relay.exe ./cmd/relay
```

`edgestpan-relay.exe` را اجرا کنید. برنامه روی TCP **8787** و UDP **8788** گوش می‌دهد. هر دو درگاه را باز کنید. نشانی رله در هر دو برنامه این است: `http://YOUR_SERVER_IP:8787`.

### ترتیب اتصال

1. هر طرف از درگاه UDP خود رله نشانی عمومی‌اش را می‌پرسد. این درخواست STUN است. کادر STUN را خالی بگذارید تا اج‌اسپن از همان رله استفاده کند. سرور STUN گوگل را وارد نکنید.
2. دو طرف این نشانی‌ها را از راه رله، همراه با رمز یا کد QR، رد و بدل می‌کنند.
3. اول یک اتصال مستقیم UDP را امتحان می‌کنند.
4. اگر تا چند ثانیه باز نشد، نشست رمزشده روی رله می‌ماند. رمز و رمزنگاری در هر دو حالت یکی است.

### رایانه با نشانی عمومی

اگر رایانه نشانی عمومی دارد، یا روتر یک درگاه UDP را به آن فرستاده است، رله لازم نیست.

- **ویندوز:** در صفحه اینترنت، **نشانی عمومی برای باز کردن درگاه** را پر کنید. درگاه `0` یعنی همان درگاهی که اج‌اسپن روی آن گوش می‌دهد. کد QR این نشانی را هم دارد.
- **اندروید:** در صفحه اینترنت، `host:port` را در **نشانی عمومی رایانه** بنویسید.

گوشی اول آن نشانی را امتحان می‌کند، بعد سوراخ‌کردن NAT، و بعد رله.

### دامنه و TLS، اگر خواستید

دامنه اختیاری است. یک رکورد A به سرور بدهید و فقط درگاه ۸۷۸۷ را پشت Caddy یا nginx بگذارید. WebSocket روی همان درگاه بالا می‌رود. UDP ۸۷۸۸ معمولی می‌ماند و پشت HTTPS نمی‌رود.

نمونه Caddyfile:

```text
relay.example.com {
    reverse_proxy 127.0.0.1:8787
}
```

در `docker-compose.yml` درگاه HTTP را به `127.0.0.1:8787:8787` محدود کنید تا فقط Caddy به آن برسد، و `8788:8788/udp` را عمومی بگذارید.

نشانی را در هر دو برنامه این‌طور بنویسید:

```text
https://relay.example.com
```
