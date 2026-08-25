OpenVPN для КЕП (Кипр)
======================

1. Положите конфиг провайдера как:
   vpn/custom.ovpn

2. В remote должен быть IP, не домен (иначе gluetun не поднимется).
   Пример: remote 195.58.46.39 20299 udp

3. Логин/пароль — в .env:
   OPENVPN_USER=...
   OPENVPN_PASSWORD=...

4. Архитектура Docker:
   - контейнер vpn (gluetun) поднимает OpenVPN + HTTP-прокси :8888
   - контейнер bot ходит в Telegram НАПРЯМУЮ
   - запросы к kep-kepo.gov.cy идут через http://vpn:8888

5. На сервере (VPS), если vpn unhealthy:

   docker compose logs vpn --tail 100

   Типичные причины:
   - EMSGSIZE / Message too large — MTU слишком большой.
     В custom.ovpn уже стоят tun-mtu 1400 + TCP :8000.
     Если всё ещё bad: уменьшите до tun-mtu 1280 / mssfix 1240.
   - нет /dev/net/tun:
       ls -l /dev/net/tun
   - в .env пустые OPENVPN_USER / OPENVPN_PASSWORD
   - после правок:
       docker compose down
       docker volume rm drivinglicense-checker_vpn-data 2>/dev/null || true
       docker compose up -d --build
       docker compose logs -f vpn

6. Когда vpn healthy (нет бесконечных restart):
   docker compose exec vpn wget -qO- https://ifconfig.me/ip
   # должен быть кипрский IP
