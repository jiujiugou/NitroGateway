# probe6.py - held-connection Modbus TCP verification.
#
# Why not probe1..5: they connect -> send ONE request -> wait 1.2s -> close.
# Witte Modbus Slave only starts serving a TCP session after the connection has
# been held for a short moment; single-shot connect/send/close consistently
# timed out. This probe opens ONE connection per slave, waits for the session
# to settle, then sends all 4 function requests on the SAME held socket.
import socket, struct, time

def build_read(tid, unit, fc, start, qty):
    pdu = struct.pack(">BHH", fc, start, qty)
    mbap = struct.pack(">HHHB", tid, 0, 6 + len(pdu), unit)
    return mbap + pdu

def recv_exact(s, n, timeout):
    s.settimeout(timeout)
    data = b""
    end = time.time() + timeout
    while len(data) < n and time.time() < end:
        try:
            chunk = s.recv(4096)
            if not chunk:
                break
            data += chunk
        except socket.timeout:
            break
    return data

def probe_slave(unit, timeout=3.0):
    """One held connection per slave; returns dict fc->result."""
    out = {}
    try:
        s = socket.create_connection(("127.0.0.1", 502), timeout=timeout)
    except Exception as e:
        return {3: "CONNECT-ERR %s" % e}
    # let the server-side session settle before sending
    time.sleep(0.3)
    for (fc, start, qty) in [(3, 0, 90), (4, 0, 6), (1, 0, 2), (2, 0, 2)]:
        tid = 0x3000 + fc
        s.sendall(build_read(tid, unit, fc, start, qty))
        try:
            data = recv_exact(s, 9, timeout)
        except ConnectionResetError:
            out[fc] = "RESET"
            continue
        if len(data) < 9:
            out[fc] = "TIMEOUT/short(%d)" % len(data)
            continue
        if data[7] & 0x80:
            out[fc] = "EXC%d" % data[8]
            continue
        bc = data[8]
        payload = data[9:]
        out[fc] = "OK bc=%d first=%s" % (bc, payload[:4].hex())
    s.close()
    return out

lines = []
for unit in range(1, 11):
    r = probe_slave(unit)
    lines.append("unit=%d %s" % (unit, " ".join("FC%d=%s" % (fc, r[fc]) for fc in (3, 4, 1, 2))))

# boundary checks (expect EXC2 one-past-end)
lines.append("BOUND unit=1 FC3 start=90 qty=1 -> %s" % probe_slave(1)[3])
lines.append("BOUND unit=1 FC4 start=6  qty=1 -> %s" % probe_slave(1)[4])

text = "\n".join(lines)
with open(r"D:\Code\NitroGateway\tools\factory-test\probe-result6.txt", "w", encoding="utf-8") as f:
    f.write(text)
print(text)
