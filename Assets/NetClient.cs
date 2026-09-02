using UnityEngine;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using UnityEngine.UIElements;
using System;
using System.Linq;
using UnityEngine.InputSystem;

public class NetClient : MonoBehaviour
{
    TcpClient client;
    NetworkStream stream;
    Thread recvThread;
    volatile bool running = true;

    // clients�� ������ �ϳ��� ������ �� �ֵ��� �� ����
    readonly object clientsLock = new object();

    // ���� ������Ʈ�� 
    Dictionary<int, GameObject> objects = new();

    struct Client
    {
        public int x;
        public int y;

        public Client(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    };
    Dictionary<int, Client> clients = new();

    // �򿣵�� ���� (C++ get_u16/get_u32 �״��)
    ushort GetU16(byte[] b, int off) => (ushort)((b[off] << 8) | b[off + 1]);
    uint GetU32(byte[] b, int off) =>
        (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

    // ���� �޼���
    void SendMove(sbyte dx, sbyte dy)
    {
        byte[] pkt = new byte[6];
        pkt[0] = 0; pkt[1] = 2; // length = 2
        pkt[2] = 0; pkt[3] = 2; // type = 2 (PKT_MOVE)
        pkt[4] = (byte)dx;
        pkt[5] = (byte)dy;
        stream.Write(pkt, 0, 6); // ���ν����� write, ��׶��� read�� ���� OK
    }

    void Start()
    {
        client = new TcpClient();
        client.Connect("127.0.0.1", 9000);
        stream = client.GetStream();
        recvThread = new Thread(RecvLoop);
        recvThread.Start();
        Debug.Log("���� ���� ����, ���� ����");
    }

    void RecvLoop()
    {
        var recvBuf = new List<byte>();   // C++�� recv_buf
        byte[] buf = new byte[4096];
        while (running)
        {
            int cnt = stream.Read(buf, 0, buf.Length);   // ����ŷ read (���� ��׶���� OK)
            if (cnt <= 0) break;
            for (int i = 0; i < cnt; i++) recvBuf.Add(buf[i]);   // append

            // ������ ����
            while (true)
            {
                if (recvBuf.Count < 4) break;
                UInt16 length = GetU16(recvBuf.ToArray(), 0);
                if (recvBuf.Count < (4 + length)) break;
                UInt16 type = GetU16(recvBuf.ToArray(), 2);

                byte[] payload = new byte[length];
                Array.Copy(recvBuf.ToArray(), 4, payload, 0, length);
                recvBuf.RemoveRange(0, 4 + length);

                if (type == 4) // type:4 - PKT_DELTA 
                {
                    lock (clientsLock)
                    {
                        int off = 0; // ������

                        // removed ��ȹ
                        int removedCnt = GetU16(payload, off);
                        off += 2;
                        for (int i = 0; i < removedCnt; i++)
                        {
                            int id = (int)GetU32(payload, off);
                            off += 4;
                            clients.Remove(id);
                        }

                        // changed ��ȹ
                        int changedCnt = GetU16(payload, off);
                        off += 2;
                        for (int i = 0; i < changedCnt; i++)
                        {
                            int id = (int)GetU32(payload, off);
                            off += 4;
                            int x = (int)GetU32(payload, off);
                            off += 4;
                            int y = (int)GetU32(payload, off);
                            off += 4;

                            clients[id] = new Client(x, y); // �߰� or ����
                        }
                    }
                }
            }
        }
    }

    void OnDestroy()   // Play ���� �� ������/���� ���� (C++�� close + join)
    {
        running = false;
        stream?.Close();
        client?.Close();
        recvThread?.Join();
    }

    // ���� �����忡�� �� ������ ���� - ���ӿ�����Ʈ�� ���⼭�� �ٷ���.
    // �� ������ ������ �� + ������Ʈ ������ ���� �� �޼��忡 ����.
    // Ŭ���̾�Ʈ �̺�Ʈ�� Update �޼��忡�� ����������, ����ŷ read(��Ʈ��ũ ����)�� �̰��� ����Ǿ� ������ ȭ���� ��������.
    // ������ ��׶��� ������(RecvLoop)�� �����س��� 
    private void Update()
    {
        // Ű���� �Է� -> �̵� ����
        var kb = Keyboard.current;
        if (kb != null)
        {
            sbyte dx = 0, dy = 0;
            if (kb.wKey.wasPressedThisFrame) dy = -1; // ���� ��ǥ �� y�� �Ʒ��� ������ Ŀ���� ���̴�. �ؼ� w ������ -1��.
            else if (kb.sKey.wasPressedThisFrame) dy = 1;
            else if (kb.aKey.wasPressedThisFrame) dx = -1;
            else if (kb.dKey.wasPressedThisFrame) dx = 1;
            if (dx != 0 || dy != 0) SendMove(dx, dy);
        }

        lock (clientsLock) { 
            // �������� �ִ� �÷��̾�: ������Ʈ ������ �����, ��ġ ����
            foreach (var kv in clients)
            {
                int id = kv.Key;
                if (!objects.ContainsKey(id))
                {
                    objects[id] = GameObject.CreatePrimitive(PrimitiveType.Cube); // �� ������Ʈ(ť��) ����
                }

                // ���� ��ǥ(0~100) -> ȭ�� ��ǥ. ���� �߽�(50, 50)�� ȭ�� �����, 0.1�� ���
                float px = (kv.Value.x - 50) * 0.1f;
                float py = -(kv.Value.y - 50) * 0.1f; // ������ �Ʒ��� +y, Unity�� ���� +y�� ��ȣ ���� �ʿ� 

                objects[id].transform.position = new Vector3(px, py, 0);
            }

            // ���������� ����� �÷��̾�(����): ������Ʈ ����
            var gone = objects.Keys.Where(id => !clients.ContainsKey(id)).ToList();
            foreach (var id in gone)
            {
                Destroy(objects[id]);
                objects.Remove(id);
            }
        }
    }
}