# CustumPutty

Windows용 설비 터미널 **EquipLink**의 소스 코드와 실행파일을 제공합니다.
PuTTY 기반 Telnet/SSH, Xming 실행 연동, FTP 파일 관리, SSH Browser를 한 프로그램에서 사용할 수 있습니다.

## 다운로드

**[Windows x64 실행파일 ZIP 다운로드](https://github.com/zenkac/CustumPutty/releases/download/v1.8.1/EquipLink-v1.8.1-Windows-x64.zip)** · [릴리스 목록](https://github.com/zenkac/CustumPutty/releases)

v1.8.1은 상위 폴더 목록 권한이 제한된 서버와 상대 파일명만 받는 구형 서버의 FTP 삭제를 개선했습니다.
삭제 실패 시 작은 창에 대상 경로·실패 단계·FTP 오류 코드를 표시하고 복사할 수 있습니다.

1. ZIP을 내려받아 압축을 풉니다.
2. 기존 EquipLink가 실행 중이면 종료합니다.
3. `EquipLink.exe`를 실행합니다. 별도 .NET 설치 없이 사용할 수 있는 포터블 배포본입니다.

기존 설비 목록·사이트맵·로그인 정보·테마는 같은 Windows 계정에서 이어서 사용합니다.
설정은 `%LOCALAPPDATA%\EquipLink`에 저장됩니다. 저장 비밀번호는 Windows 계정으로 암호화합니다.

소스만 필요하다면 **Code → Download ZIP**을 선택하거나 아래 명령으로 복제하세요.

```powershell
git clone https://github.com/zenkac/CustumPutty.git
cd CustumPutty
```

## 주요 기능

- Telnet 기본 포트 23 / SSH 기본 포트 22, 설비 목록 더블클릭 접속, 저장 계정 자동 로그인
- 터미널 탭, 가로·세로 분할, 세 가지 테마, 명령 입력창
- 기존 Xming/XLaunch 설치 연동과 X11 명령 준비
- 일반 FTP, 사이트맵, 경로 입력·폴더 탐색, 업로드·다운로드·전송 이력
- Windows 파일 아이콘, 정렬·열 너비 조절이 가능한 목록, 내 PC/서버 폴더 트리
- PC/서버 좌우 위치 전환, 선택한 위치를 넓게 보는 한 창/두 창 보기
- 양쪽 창 사이 파일·폴더 드래그 전송, 목적지 폴더 강조와 전송 방향 표시
- 아이콘/목록 보기, 현재 폴더 검색, Ctrl/Shift 및 드래그 영역 선택
- Windows 탐색기와 드래그 전송, 파일 더블클릭 다운로드, 중복 파일 확인
- 영구 삭제 경고 팝업, 파일·폴더 삭제
- SSH 연결의 SFTP Browser

## 경유 설비에서 FTP 사용

`내 PC → Telnet 설비1 → Telnet 설비2`처럼 접속할 때에는 FTP 화면의 **내 PC** 선택창에서
해당 **터미널 FTP** 탭을 선택할 수 있습니다.

`ftp -i`, `lcd`, `cd`, `put/mput/get/mget` 명령을 기존 터미널의 명령 입력창에 준비합니다.
현재 접속 위치와 프롬프트를 확인하고 직접 실행하세요. 로그인도 터미널에서 진행합니다.

설비2의 파일 목록·전송 상태는 터미널에서 확인하는 방식이며, 앱의 전송 대기열에 연동되지 않습니다.
상세한 순서와 설정은 [사용설명서](README.txt)를 참고하세요.

## 개발 환경 및 빌드

- Windows x64
- .NET 10 SDK
- PowerShell

```powershell
# 개발용 빌드
.\build.ps1

# 런타임을 포함한 단일 EXE 배포용 빌드
.\build.ps1 -Portable
```

배포용 결과: `artifacts\portable-v1.8.1\EquipLink.exe`

`src/packages.lock.json`에 의존성 버전이 기록되어 있습니다.
`vendor/nuget`에는 앱이 사용하는 네 개의 NuGet 패키지가 포함되어 있습니다.
SDK·런타임 팩이 설치되어 있지 않다면 최초 복원 시 인터넷 연결이 필요할 수 있습니다.

## 테스트

포터블 빌드 후 다음 명령은 합성 파일과 로컬 시험 서버를 사용해 FTP 기능을 검사합니다.

```powershell
.\tests\test.ps1
```

SSH/SFTP 및 로그인 검사는 Python과 Paramiko가 추가로 필요합니다.

```powershell
py -m venv .test-venv
.\.test-venv\Scripts\python.exe -m pip install -r tests\requirements.txt
$env:EQUIPLINK_TEST_PYTHON = (Resolve-Path .\.test-venv\Scripts\python.exe).Path
.\tests\test.ps1 -Suite sftp,login
```

시험 결과와 생성 데이터는 Git에서 제외되는 `artifacts` 아래에 저장됩니다.
공개 소스의 SSH 시험 경로는 개인 PC 경로 대신 환경변수를 사용하도록 정리했습니다.
ZIP 안에 최종 실행파일의 검증 결과와 SHA-256이 포함되어 있습니다.
실제 회사 설비·방화벽·경유 설비의 FTP 호환성은 현장에서 확인해야 합니다.

## 구성과 라이선스 고지

| 경로 | 내용 |
| --- | --- |
| `src/` | WPF 앱과 C# 검사 코드 |
| `tests/` | 테스트 실행 스크립트와 로컬 SSH/SFTP 시험 서버 |
| `vendor/` | 공식 PuTTY 실행파일 및 빌드 의존성 |
| `licenses/` | 포함된 외부 구성요소의 라이선스와 고지 |
| `README.txt` | 상세 사용설명서 |

PuTTY 0.85, .NET, SSH.NET 등의 고지는 [THIRD-PARTY-SOFTWARE](licenses/THIRD-PARTY-SOFTWARE.txt)를 참고하세요.
Xming/VcXsrv는 배포본에 포함되지 않습니다. 기존 설치를 연결해서 사용합니다.
이 프로젝트는 PuTTY/Xming/MobaXterm 개발사의 공식 제품이 아닙니다.
