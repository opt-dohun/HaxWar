# 🛠️ .NET CLI 자주 사용하는 명령어 가이드 (DOTNET_GUIDE)

.NET 개발 및 프로젝트 운영 시 터미널에서 자주 사용하는 .NET CLI(명령줄 인터페이스) 명령어들을 정리한 문서입니다. 모든 명령어는 솔루션 파일(`.sln`) 또는 프로젝트 파일(`.csproj`)이 있는 경로에서 실행하는 것이 기준입니다.

---

## 📂 1. 프로젝트 및 솔루션 관리

### 1.1 솔루션 파일 생성 및 관리
* **새 솔루션 파일 생성**
  ```bash
  dotnet new sln -n [솔루션이름]
  ```
* **솔루션에 프로젝트 추가**
  ```bash
  dotnet sln add [프로젝트경로/이름.csproj]
  ```
  *예시:* `dotnet sln add src/HexWar.Domain/HexWar.Domain.csproj`
* **솔루션에서 프로젝트 제거**
  ```bash
  dotnet sln remove [프로젝트경로/이름.csproj]
  ```

### 1.2 새 프로젝트 생성 (`dotnet new`)
* **새 콘솔 애플리케이션 생성**
  ```bash
  dotnet new console -n [프로젝트이름] -o [출력경로]
  ```
* **새 웹 API 프로젝트 생성 (ASP.NET Core)**
  ```bash
  dotnet new webapi -n [프로젝트이름]
  ```
* **새 클래스 라이브러리 생성 (비즈니스 로직용)**
  ```bash
  dotnet new classlib -n [프로젝트이름]
  ```

### 1.3 프로젝트 간 참조(Reference) 설정
* **A 프로젝트가 B 라이브러리를 참조하도록 설정**
  ```bash
  dotnet add [A프로젝트.csproj] reference [B프로젝트.csproj]
  ```
  *예시:* `dotnet add src/HexWar.Server/HexWar.Server.csproj reference src/HexWar.Domain/HexWar.Domain.csproj`
* **프로젝트 참조 제거**
  ```bash
  dotnet remove [A프로젝트.csproj] reference [B프로젝트.csproj]
  ```

---

## 📦 2. 패키지(NuGet) 관리

### 2.1 패키지 설치 및 제거
* **NuGet 패키지 설치**
  ```bash
  dotnet add [프로젝트.csproj] package [패키지명]
  ```
  *특정 버전 설치 예시:* `dotnet add src/HexWar.Domain/HexWar.Domain.csproj package protobuf-net --version 3.2.56`
* **NuGet 패키지 제거**
  ```bash
  dotnet remove [프로젝트.csproj] package [패키지명]
  ```

### 2.2 패키지 조회 및 복원
* **현재 프로젝트에 설치된 NuGet 패키지 목록 확인**
  ```bash
  dotnet list [프로젝트.csproj] package
  ```
* **솔루션 전체의 의존성/패키지 복원 (보통 빌드 시 자동 수행)**
  ```bash
  dotnet restore
  ```

---

## 🚀 3. 빌드 및 실행

### 3.1 프로젝트 빌드 및 청소
* **솔루션/프로젝트 빌드**
  ```bash
  dotnet build
  ```
  *배포/Release 모드로 빌드:* `dotnet build -c Release`
* **빌드 결과물(bin, obj 폴더) 삭제 및 초기화**
  ```bash
  dotnet clean
  ```

### 3.2 애플리케이션 실행
* **프로젝트 실행**
  ```bash
  dotnet run --project [실행할프로젝트.csproj]
  ```
  *Release 모드로 실행:* `dotnet run -c Release --project [프로젝트.csproj]`
* **핫 리로드(Hot Reload) 실행 (코드 수정 시 실시간 자동 반영)**
  ```bash
  dotnet watch --project [실행할프로젝트.csproj]
  ```

### 3.3 배포용 빌드 (`Publish`)
* **특정 플랫폼에 최적화된 실행 파일 배포 빌드**
  ```bash
  dotnet publish [프로젝트.csproj] -c Release -o [배포경로]
  ```
  *자가 포함형 단일 실행파일(Self-contained Single File) 배포 예시:*
  ```bash
  dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
  ```
  *(OS 식별자 예: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`)*

---

## 🧪 4. 테스트 및 진단

### 4.1 단위 및 통합 테스트 실행
* **솔루션 내 모든 테스트 코드 실행**
  ```bash
  dotnet test
  ```
* **실패한 테스트만 재실행**
  ```bash
  dotnet test --failed
  ```

### 4.2 개발용 인증서 관리 (HTTPS 웹 개발 필수)
* **로컬 개발용 루프백 HTTPS 인증서 신뢰 설정**
  ```bash
  dotnet dev-certs https --trust
  ```

---

## 🛠️ 5. 유용한 글로벌 도구 (Global Tools)

.NET은 CLI 환경에서 쓸 수 있는 강력한 진단/개발 툴들을 설치해서 쓸 수 있습니다.

* **EF Core 마이그레이션 도구 설치**
  ```bash
  dotnet tool install --global dotnet-ef
  ```
* **서버 메모리 덤프 수집 도구 (성능 병목/메모리 누수 진단용)**
  ```bash
  dotnet tool install --global dotnet-dump
  ```
* **서버 CPU 프로파일러 수집 도구 (CPU 병목 분석용)**
  ```bash
  dotnet tool install --global dotnet-trace
  ```
* **실시간 성능 카운터 수집 도구 (GC 힙 크기, CPU 사용량 모니터링)**
  ```bash
  dotnet tool install --global dotnet-counters
  ```
  *실시간 실행 예시:* `dotnet-counters monitor -p [프로세스ID]`
