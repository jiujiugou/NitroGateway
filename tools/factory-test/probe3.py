import socket, struct, time
def build_read(tid, unit, fc, start, qty):
    pdu = struct.pack(">BHH", fc, start, qty)
    mbap = struct.pack(">HHHB", tid, 0, 6 + len(pdu), unit)
    return mbap + pdu
def probe_one(unit, fc, start, qty, timeout=1.2):
    try:
        s = socket.create_connection(("127.0.0.1", 502), timeout=timeout)
        s.settimeout(timeout)
        s.sendall(build_read(0x1000 + unit, unit, fc, start, qty))
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
        if len(data) < 9: return "TIMEOUT/short(%d)" % len(data)
        if data[7] & 0x80: return "EXC%d" % data[8]
        return "OK bc=%d first=%s" % (data[8], data[9:13].hex())
    except Exception as e:
        return "ERR %s" % e
lines = []
for (unit, fc, start, qty) in [(1,3,0,90),(1,3,0,10),(1,1,0,2),(2,3,0,10)]:
    r = probe_one(unit, fc, start, qty)
    lines.append("unit=%d FC%d start=%d qty=%d -> %s" % (unit, fc, start, qty, r))
with open(r"D:\Code\NitroGateway\tools\factory-test\probe-result3.txt", "w") as f:
    f.write("\n".join(lines))
