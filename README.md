# game-client-unity

[game-server](https://github.com/myandue/game-server)의 Unity(C#) 클라이언트. 서버가 보내는 위치 델타를 받아 플레이어를 큐브로 그리고, wasd 입력을 이동 패킷으로 보낸다. 서버 쪽 설계와 벤치는 서버 저장소 README에 있다.

## 실행
1. 서버 실행: game-server 저장소에서 `./game_server` (포트 9000)
2. Unity 6000.0(2D URP)로 이 프로젝트를 열고 SampleScene에서 Play
3. Game 뷰를 클릭해 포커스한 뒤 wasd로 이동. 콘솔 클라이언트나 봇을 같이 붙이면 다른 큐브가 보인다
- 서버 주소·포트는 `Assets/NetClient.cs` 55행 `client.Connect("127.0.0.1", 9000)`에 고정돼 있다. 다른 서버에 붙이려면 이 값을 바꾼다.

## 구조
- **수신**: 백그라운드 스레드 `RecvLoop`가 TcpClient 스트림을 읽어 `[length:u16][type:u16][payload]` 프레임을 재조립·디코드하고, lock 안에서 `clients` 딕셔너리에 저장
- **렌더**: 메인 스레드 `Update`가 lock 안에서 `clients`를 읽어 GameObject를 생성·이동하고, 목록에 없는 id의 큐브는 제거
- **입력**: `Update` 맨 위에서 new Input System(`Keyboard.current`)으로 wasd를 읽어 6바이트 MOVE 패킷 `[len=2][type=2][dx][dy]`를 전송
- **좌표**: 서버 격자 좌표를 `(x-50)*0.1`, `-(y-50)*0.1`로 화면에 매핑(화면 위가 -y)

## 설계 결정
- **수신을 스레드로 뺀 이유**: 메인 스레드에서 블로킹 read를 하면 프레임이 멈춘다. 소켓 읽기는 백그라운드에서, 오브젝트 생성·이동은 메인 스레드에서만 한다(Unity API는 메인 스레드 전용)
- **델타는 교체가 아니라 적용**: PKT_DELTA의 removed는 딕셔너리에서 제거, changed는 덮어쓰기. 매 패킷마다 Clear하지 않는다. 첫 접속 때는 서버가 빈 상태와의 차이를 보내므로 전체가 spawn된다
- **클라이언트는 의도만 보낸다**: 위치를 스스로 바꾸지 않고 서버가 보낸 상태를 그대로 그린다(server-authoritative). 서버가 거부한 이동은 화면에서도 움직이지 않는다

## 프로토콜 (서버와 동일한 계약)
| type | 방향 | payload |
|---|---|---|
| 2 MOVE | 클라 → 서버 | dx:i8, dy:i8 |
| 4 DELTA | 서버 → 클라 | removed_cnt:u16, id:u32 × n, changed_cnt:u16, (id:u32, x:i32, y:i32) × m |

클라이언트는 type 4(DELTA)만 처리하고, 그 밖의 프레임(TICK 1 등)은 길이만큼 읽어서 버린다.

## 한계
- 보간·예측 없음(50ms tick마다 위치가 점프), 재접속 처리 없음
- 상태 공유가 단일 lock. 플레이어 수가 많아지면 수신 스레드가 lock을 잡는 시간이 프레임에 영향을 줄 수 있다
