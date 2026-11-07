#!/usr/bin/env python3
"""linkemu: a TCP relay that behaves like a slower, farther network link.

    linkemu.py LISTEN_PORT TARGET_HOST TARGET_PORT RTT_MS MBIT

Every byte is delayed by RTT/2 in each direction and each direction is
limited to MBIT megabits/s (serialisation, like a real link). Used by the
benchmark lab where the kernel has no netem. Like the GT VPN client (a TLS
tunnel), it acknowledges data locally, so the ssh connection inside sees
the link's latency and bandwidth, not its TCP window.
"""
import asyncio
import sys
import time

LPORT, HOST, PORT = int(sys.argv[1]), sys.argv[2], int(sys.argv[3])
HALF = float(sys.argv[4]) / 2000.0
RATE = float(sys.argv[5]) * 1e6 / 8  # bytes/s


async def pump(reader, writer):
    q = asyncio.Queue()
    free = [0.0]

    async def rx():
        while True:
            b = await reader.read(65536)
            t = time.monotonic()
            if not b:
                await q.put((t + HALF, None)); return
            free[0] = max(free[0], t) + len(b) / RATE
            await q.put((free[0] + HALF, b))

    async def tx():
        while True:
            due, b = await q.get()
            d = due - time.monotonic()
            if d > 0:
                await asyncio.sleep(d)
            if b is None:
                try: writer.write_eof()
                except OSError: pass
                return
            writer.write(b)
            await writer.drain()

    await asyncio.gather(rx(), tx(), return_exceptions=True)
    writer.close()


async def handle(cr, cw):
    sr, sw = await asyncio.open_connection(HOST, PORT)
    for s in (cw, sw):
        sock = s.get_extra_info("socket")
        import socket
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    await asyncio.gather(pump(cr, sw), pump(sr, cw))


async def main():
    srv = await asyncio.start_server(handle, "127.0.0.1", LPORT)
    async with srv:
        await srv.serve_forever()

asyncio.run(main())
