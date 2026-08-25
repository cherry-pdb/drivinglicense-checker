OpenVPN для КЕП (Кипр)
======================

1. Положите конфиг провайдера как:
   vpn/custom.ovpn

2. В remote должен быть IP, не домен (иначе gluetun не поднимется).
   Пример: remote 195.58.46.39 20299

3. Логин/пароль — в .env:
   OPENVPN_USER=...
   OPENVPN_PASSWORD=...

4. Архитектура Docker:
   - контейнер vpn (gluetun) поднимает OpenVPN + HTTP-прокси :8888
   - контейнер bot ходит в Telegram НАПРЯМУЮ
   - запросы к kep-kepo.gov.cy идут через http://vpn:8888

5. Проверка VPN IP:
   docker compose exec vpn wget -qO- https://ifconfig.me/ip
