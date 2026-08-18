import socket, struct, time

def build_read(tid, unit, fc, start, qty):
    pdu = struct.pack(">BHH", fc, start, qty)
    mbap = struct.pack(">HHHB", tid, 0, 6 + len(pdu), unit)
    return mbap + pdu

def probe_one(unit, fc, start, qty, timeout=1.0):
    try:
        s = socket.create_connection(("127.0.0.1", 502), timeout=timeout)
        s.settimeout(timeout)
        s.sendall(build_read(0x2000 + unit, unit, fc, start, qty))
        data = b""
        end = time.time() + timeout
        while len(data) < 9 and time.time() < end:
            try:
                chunk = s.recv(4096)
                if not chunk: break
                data += chunk
            except socket.timeout:
                break
        s.close()
        if len(data) < 9:
            return "TIMEOUT/short(%d)" % len(data)
        if data[7] & 0x80:
            return "EXC%d" % data[8]
        return "OK bc=%d first=%s" % (data[8], data[9:13].hex())
    except Exception as e:
        return "ERR %s" % e

lines = []
# full sweep: 10 units x 4 FCs
for unit in range(1, 11):
    for (fc, start, qty) in [(3, 0, 90), (4, 0, 6), (1, 0, 2), (2, 0, 2)]:
        lines.append("u%d FC%d s%d q%d -> %s" % (unit, fc, start, qty, probe_one(unit, fc, start, qty)))
lines.append("--- held sequential (unit=1) ---")
for qty in (4, 90):
    lines.append("fc3 qty%d -> %s" % (qty, probe_one(1, 3, 0, qty)))

with open(r"D:\Code\NitroGateway\tools\factory-test\diag1.txt", "w") as f:
    f.write("\n".join(lines))
print("\n".join(lines))
