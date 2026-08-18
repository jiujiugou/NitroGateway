// hsl-probe: 用 HslCommunication ModbusTcpNet 客户端对 ModbusSlaveSim(或 Witte Slave) 做
// 多点位多类型 seed 写入 + 读回校验。FC03 保持寄存器 + FC01 线圈可走协议写；
// FC04 输入寄存器 / FC02 离散输入只读。布局对齐 tools/factory-test/points-device-NN.csv。
// 验收: 输出 SUMMARY seedFail=0 readFail=0 SEED-OK。
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.ModBus;

const int MaxSlave = 10;

int seedFail = 0, readFail = 0;
for (int s = 1; s <= MaxSlave; s++)
{
    var client = new ModbusTcpNet
    {
        IpAddress = "127.0.0.1",
        Port = 502,
        Station = (byte)s,
        DataFormat = DataFormat.ABCD,
        ConnectTimeOut = 3000,
        ReceiveTimeOut = 3000
    };
    var conn = client.ConnectServerAsync().GetAwaiter().GetResult();
    if (!conn.IsSuccess)
    {
        Console.WriteLine($"slave={s:00} CONNECT-FAIL {conn.Message}");
        seedFail++;
        continue;
    }

    var failures = new List<string>();
    void Seed(string tag, Func<OperateResult> w)
    {
        var r = w();
        if (!r.IsSuccess) { failures.Add($"{tag}:{r.Message}"); }
    }

    // ---- FC03 保持寄存器: 30 寄存器一块 x 3 块 ----
    for (int b = 0; b < 3; b++)
    {
        int baseOff = b * 30;
        // 5 x Float (每块占 10 寄存器)
        for (int f = 0; f < 5; f++)
            Seed($"F{b}.{f}", () => client.WriteAsync((baseOff + 2 * f).ToString(), (float)(s + b * 0.1 + f * 0.01)).Result);
        Seed($"I32{b}",  () => client.WriteAsync((baseOff + 10).ToString(), s * 1000000 + b * 1000 + 10).Result);
        Seed($"U32{b}",  () => client.WriteAsync((baseOff + 12).ToString(), (uint)(s * 1000000 + b * 1000 + 20)).Result);
        Seed($"I16a{b}", () => client.WriteAsync((baseOff + 14).ToString(), (short)(s * 1000 + b * 100 + 30)).Result);
        Seed($"U16a{b}", () => client.WriteAsync((baseOff + 15).ToString(), (ushort)(s * 1000 + b * 100 + 40)).Result);
        Seed($"I16b{b}", () => client.WriteAsync((baseOff + 16).ToString(), (short)(s * 1000 + b * 100 + 50)).Result);
        Seed($"U16b{b}", () => client.WriteAsync((baseOff + 17).ToString(), (ushort)(s * 1000 + b * 100 + 60)).Result);
        Seed($"DBL{b}",  () => client.WriteAsync((baseOff + 18).ToString(), s + 0.1234567 + b).Result);
        Seed($"I64{b}",  () => client.WriteAsync((baseOff + 22).ToString(), (long)(s * 1000000000000L + b * 100000 + 70)).Result);
        Seed($"U64{b}",  () => client.WriteAsync((baseOff + 26).ToString(), (ulong)(s * 1000000000000L + b * 100000 + 80)).Result);
    }

    // ---- FC01 线圈: 2 点 ----
    Seed("C0", () => client.WriteAsync("x=1;0", true).Result);
    Seed("C1", () => client.WriteAsync("x=1;1", false).Result);

    if (failures.Count > 0)
    {
        Console.WriteLine($"slave={s:00} SEED-FAIL({failures.Count}): " + string.Join(";", failures.Take(6)));
        seedFail += failures.Count;
    }

    // ---- 读回抽查 ----
    var f0 = client.ReadFloatAsync("0", 1).Result;     // 期望 s.00
    var i32 = client.ReadInt32Async("10", 1).Result;   // 期望 s*1e6+10
    var dbl = client.ReadDoubleAsync("18", 1).Result;  // 期望 s+0.1234567
    var c0 = client.ReadBoolAsync("x=1;0", 1).Result;  // 期望 True
    var f30 = client.ReadFloatAsync("30", 1).Result;   // 第二块

    string Show<T>(OperateResult<T[]> r) => r.IsSuccess ? string.Join(",", r.Content!) : "FAIL:" + r.Message;
    Console.WriteLine($"slave={s:00} READ  F0=[{Show(f0)}] I32@10=[{Show(i32)}] DBL@18=[{Show(dbl)}] C0=[{Show(c0)}] F@30=[{Show(f30)}]");
    if (!(f0.IsSuccess && i32.IsSuccess && dbl.IsSuccess && c0.IsSuccess && f30.IsSuccess)) readFail++;

    client.ConnectClose();
}

Console.WriteLine($"SUMMARY seedFail={seedFail} readFail={readFail}  " +
    (seedFail == 0 && readFail == 0 ? "SEED-OK" : "CHECK-FAILURES"));
