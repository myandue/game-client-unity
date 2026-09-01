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

    // clients에 스레드 하나만 접근할 수 있도록 락 생성
    readonly object clientsLock = new object();

    // 게임 오브젝트들 
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

    // 빅엔디안 헬퍼 (C++ get_u16/get_u32 그대로)
    ushort GetU16(byte[] b, int off) => (ushort)((b[off] << 8) | b[off + 1]);
    uint GetU32(byte[] b, int off) =>
        (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

    // 전송 메서드
    void SendMove(sbyte dx, sbyte dy)
    {
        byte[] pkt = new byte[6];
        pkt[0] = 0; pkt[1] = 2; // length = 2
        pkt[2] = 0; pkt[3] = 2; // type = 2 (PKT_MOVE)
        pkt[4] = (byte)dx;
        pkt[5] = (byte)dy;
        stream.Write(pkt, 0, 6); // 메인스레드 write, 백그라운드 read와 동시 OK
    }

    void Start()
    {
        client = new TcpClient();
        client.Connect("127.0.0.1", 9000);
        stream = client.GetStream();
        recvThread = new Thread(RecvLoop);
        recvThread.Start();
        Debug.Log("서버 접속 성공, 수신 시작");
    }

    void RecvLoop()
    {
        var recvBuf = new List<byte>();   // C++의 recv_buf
        byte[] buf = new byte[4096];
        while (running)
        {
            int cnt = stream.Read(buf, 0, buf.Length);   // 블로킹 read (여긴 백그라운드라 OK)
            if (cnt <= 0) break;
            for (int i = 0; i < cnt; i++) recvBuf.Add(buf[i]);   // append

            // 프레임 추출
            while (true)
            {
                if (recvBuf.Count < 4) break;
                UInt16 length = GetU16(recvBuf.ToArray(), 0);
                if (recvBuf.Count < (4 + length)) break;
                UInt16 type = GetU16(recvBuf.ToArray(), 2);

                Debug.Log("frame len=" + length);

                byte[] payload = new byte[length];
                Array.Copy(recvBuf.ToArray(), 4, payload, 0, length);
                recvBuf.RemoveRange(0, 4 + length);

                if (type == 3)
                {
                    lock (clientsLock)
                    {
                        clients.Clear();
                        int clientCnt = GetU16(payload.ToArray(), 0);

                        for (int i = 0; i < clientCnt; i++)
                        {
                            int j = 3 * i;
                            int id = (int)GetU32(payload, 4 * j + 2);
                            int x = (int)GetU32(payload, 4 * (j + 1) + 2);
                            int y = (int)GetU32(payload, 4 * (j + 2) + 2);

                            clients[id] = new Client(x, y);
                        }
                        Debug.Log("clients=" + clients.Count);
                    }
                }
            }
        }
    }

    void OnDestroy()   // Play 멈출 때 스레드/소켓 정리 (C++의 close + join)
    {
        running = false;
        stream?.Close();
        client?.Close();
        recvThread?.Join();
    }

    // 메인 스레드에서 매 프레임 실행 - 게임오브젝트는 여기서만 다뤄짐.
    // 매 프레임 반응할 것 + 오브젝트 만지는 것이 이 메서드에 구현.
    // 클라이언트 이벤트는 Update 메서드에서 구현되지만, 블로킹 read(네트워크 수신)가 이곳에 선언되어 있으면 화면이 얼어버린다.
    // 때문에 백그라운드 스레드(RecvLoop)에 구현해놓음 
    private void Update()
    {
        // 키보드 입력 -> 이동 전송
        var kb = Keyboard.current;
        if (kb != null)
        {
            sbyte dx = 0, dy = 0;
            if (kb.wKey.wasPressedThisFrame) dy = -1; // 서버 좌표 상 y는 아래로 갈수록 커지는 것이다. 해서 w 누르면 -1임.
            else if (kb.sKey.wasPressedThisFrame) dy = 1;
            else if (kb.aKey.wasPressedThisFrame) dx = -1;
            else if (kb.dKey.wasPressedThisFrame) dx = 1;
            if (dx != 0 || dy != 0) SendMove(dx, dy);
        }

        lock (clientsLock) { 
            // 스냅샷에 있는 플레이어: 오브젝트 없으면 만들고, 위치 갱신
            foreach (var kv in clients)
            {
                int id = kv.Key;
                if (!objects.ContainsKey(id))
                {
                    objects[id] = GameObject.CreatePrimitive(PrimitiveType.Cube); // 새 오브젝트(큐브) 생성
                    Debug.Log("cube 생성 id=" + id);
                }

                // 서버 좌표(0~100) -> 화면 좌표. 월드 중심(50, 50)을 화면 가운데로, 0.1배 축소
                float px = (kv.Value.x - 50) * 0.1f;
                float py = -(kv.Value.y - 50) * 0.1f; // 서버는 아래가 +y, Unity는 위가 +y라서 부호 반전 필요 

                objects[id].transform.position = new Vector3(px, py, 0);
            }

            // 스냅샷에서 사라진 플레이어(나감): 오브젝트 제거
            var gone = objects.Keys.Where(id => !clients.ContainsKey(id)).ToList();
            foreach (var id in gone)
            {
                Destroy(objects[id]);
                objects.Remove(id);
            }
            Debug.Log("objects=" + objects.Count);
        }
    }
}