# Full-range probe: 10 slaves x 4 FCs, plus boundary/overshoot cases.
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
        if len(data) < 9:
            return "TIMEOUT/short(%d)" % len(data)
        if data[7] & 0x80:
            return "EXC%d" % data[8]
        return "OK bc=%d first=%s" % (data[8], data[9:13].hex())
    except Exception as e:
        return "ERR %s" % e

lines = []
# 10 slaves x 4 FCs (the FACTORY-TEST layout)
for unit in range(1, 11):
    lines.append("unit=%d FC3(HR) qty90 -> %s" % (unit, probe_one(unit, 3, 0, 90)))
for unit in range(1, 11):
    lines.append("unit=%d FC4(IR) qty6  -> %s" % (unit, probe_one(unit, 4, 0, 6)))
for unit in range(1, 11):
    lines.append("unit=%d FC1(Coil) qty2 -> %s" % (unit, probe_one(unit, 1, 0, 2)))
for unit in range(1, 11):
    lines.append("unit=%d FC2(DI) qty2  -> %s" % (unit, probe_one(unit, 2, 0, 2)))
# boundary: exact end + one-past-end (expect EXC2)
lines.append("unit=1 FC3 start=89 qty=1 -> %s" % probe_one(1, 3, 89, 1))
lines.append("unit=1 FC3 start=90 qty=1 -> %s" % probe_one(1, 3, 90, 1))
lines.append("unit=1 FC4 start=5 qty=1  -> %s" % probe_one(1, 4, 5, 1))
lines.append("unit=1 FC1 start=1 qty=1  -> %s" % probe_one(1, 1, 1, 1))
lines.append("unit=1 FC2 start=1 qty=1  -> %s" % probe_one(1, 2, 1, 1))
lines.append("unit=2 FC3 start=0 qty=91 -> %s" % probe_one(2, 3, 0, 91))

with open(r"D:\Code\NitroGateway\tools\factory-test\probe-result5.txt", "w") as f:
    f.write("\n".join(lines))
print("\n".join(lines))
