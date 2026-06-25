# 📂 opt-dohun 개인 프로젝트 포트폴리오 (Portfolio)

백엔드 개발자 **김도훈(opt-dohun)**의 주요 백엔드 및 시스템 엔지니어링 개인 프로젝트 목록입니다. 각 프로젝트는 성능 최적화, 로우레벨 통신, 분산 환경 동기화 등 백엔드의 핵심 기술적 문제를 해결하는 데 중점을 두었습니다.

---

## 🎮 1. HexWar (분산 기반 실시간 멀티플레이 게임 서버)
- **GitHub 저장소:** [HexWar (HaxWar)](https://github.com/opt-dohun/HaxWar) (현재 저장소)
- **기술 스택:** `C# / .NET Core`, `gRPC`, `WebSockets`, `Redis (SetNX, Pub/Sub)`, `Nginx`, `Docker Compose`

### 💡 기술적 고민 및 해결
* **Stateless 분산 환경 동기화:** 2,000명 동시 접속 타깃의 분산 환경에서 발생하는 상태 불일치 문제를 해결하기 위해 게임 상태를 Redis에 JSON으로 영속화하고, `SetNX` 분산 락으로 동시성을 제어했습니다.
* **네트워크 순단 대처 및 DB 부하 감소:** Redis Pub/Sub의 휘발성 문제를 보완하고자 서버 로컬 메모리에 고정 크기 원형 큐(Circular Buffer)를 구현하여 재접속 시 외부 DB 접근 없이 즉시 유실 패킷을 복구하도록 설계했습니다.
* **힙 할당 및 GC 오버헤드 85.7% 감축:** 초당 수만 건의 패킷 처리 중 발생하는 힙 메모리 파편화를 방지하기 위해 `ArrayPool` 및 `Memory<byte>`, `ReadOnlySpan<byte>` 기반의 Zero-allocation 구조를 구축했습니다. 이를 통해 시스템 중단을 유발하는 Gen 2 Full GC 발생 횟수를 7회에서 1회로 대폭 감소시켰습니다.

---

## 📟 2. go-rest-example (고성능 IoT 디바이스 관리 REST API)
- **GitHub 저장소:** [go-rest-example](https://github.com/opt-dohun/go-rest-example)
- **기술 스택:** `Go (Golang)`, `Gin Web Framework`, `MySQL`, `Viper`, `Zap Logger`, `Docker Compose`

### 💡 기술적 고민 및 해결
* **대량의 IoT 디바이스 정보 관리:** 디바이스의 제품 번호, Mac 주소, 펌웨어 버전 정보 및 실시간 연결 상태(`LastSeenAt`, `Status`)를 효과적으로 관리하기 위해 고성능 인덱싱 전략(복합 인덱스 및 헬스 체크 인덱스)을 MySQL에 적용했습니다.
* **컨텍스트 기반 Graceful Shutdown:** 서버 종료 시 진행 중인 요청의 누락을 방지하기 위해 Go의 `signal.NotifyContext`를 활용해 시스템 시그널(`SIGINT`, `SIGTERM`) 발생 시 안전하게 연결을 닫는 흐름을 구축했습니다.
* **환경 격리 및 로깅 고도화:** `Viper`와 `godotenv`를 통한 환경 변수 관리 및 `Zap` 구조화 로거(Structured Logger)를 도입해 운영 환경에 따른 로그 레벨 격리와 빠른 디버깅 환경을 지원합니다.

---

## 💬 3. .Net-Socket-ChatServer (로우레벨 TCP 소켓 채팅 서버)
- **GitHub 저장소:** [.Net-Socket-ChatServer](https://github.com/opt-dohun/.Net-Socket-ChatServer)
- **기술 스택:** `C# / .NET`, `System.Net.Sockets`, `Async/Await Task`

### 💡 기술적 고민 및 해결
* **TCP 패킷 단편화 및 병합(Nagle 알고리즘) 대응:** 경계가 없는 TCP 스트림 환경에서 패킷 유실 및 병합 오류를 방지하기 위해 헤더에 페이로드 크기를 명시하는 고유의 **메시지 프로토콜(MessageProtocol)**을 정의하여 수신 버퍼에서 데이터를 정확히 슬라이싱했습니다.
* **비동기 소켓 I/O를 통한 스레드 풀 고갈 방지:** 다중 클라이언트 접속 시 스레드 풀의 고갈을 막기 위해 C#의 비동기 소켓 통신 API를 적용하여 비동기 논블로킹 방식으로 클라이언트의 메시지 브로드캐스트와 연결 상태를 관리합니다.
* **세션 라이프사이클 관리:** 클라이언트 세션(`ClientSession`) 객체를 추상화하여 안전한 세션 추가/삭제 이벤트 흐름을 제공하고 소켓의 비정상 종료 시 자원을 즉시 정리하는 예외 처리를 반영했습니다.
