# PoE2 Ritual Checker

한국어 **컨트롤러 UI**의 의식 보상을 인식하고 아이템별 묶음 시세를 표시하는 Windows 앱입니다. CPU 기반 로컬 영상 인식과 Windows OCR을 사용합니다.

**현재 버전: 0.1.1 — 개발 프리릴리스.** 제공된 개발 캡처의 회귀 검사는 통과했으나 전체 아이템·HDR·모든 화면 환경의 정확도가 입증된 제품은 아닙니다.

## 다운로드 및 실행

[GitHub Releases](https://github.com/Dev-Jahn/poe2-ritual-checker/releases)에서 `RitualChecker-0.1.1-win-x64.exe` 하나를 내려받아 쓰기 가능한 폴더에서 실행하세요. DLL 폴더나 별도 .NET 설치는 필요 없습니다.

- Windows 10 2004 이상 또는 Windows 11, x64.
- 한국어 툴팁 판독에는 Windows 한국어 OCR 언어 기능이 필요합니다.
- XInput 호환 컨트롤러 기준입니다. 리그를 선택한 뒤 의식 창에서 **F8** 또는 **LB+RB를 길게 누르기**로 분석합니다.
- 아이템 위에 아날로그 커서를 올리면 옵션과 공물 비용을 읽습니다. 키와 버튼 조합은 입력 설정에서 변경합니다.
- 저장된 화면은 **화면 파일 열기**로 확인합니다. 미리보기는 의식 창을 확대합니다.

실행 파일은 현재 Authenticode 서명되지 않았습니다. 게시된 SHA-256과 빌드 출처 증명을 확인할 수 있으며, 이는 Windows 코드 서명을 대체하지 않습니다.

```powershell
Get-FileHash ./RitualChecker-0.1.1-win-x64.exe -Algorithm SHA256
# GitHub CLI가 설치되어 있으면 빌드 출처 확인:
gh attestation verify ./RitualChecker-0.1.1-win-x64.exe --repo Dev-Jahn/poe2-ritual-checker
```

## 기능

- 의식 창과 격자 검출 → 아이템 식별 → 시세 조회 → 입력을 통과시키는 오버레이.
- 묶음 전체 가격이 1 div 미만이면 ex, 이상이면 div. 환율이 없으면 임의 환산하지 않습니다.
- 징조 등 커런시는 PoE2Scout 자료를 10분간, 고유 매물은 15분간 재사용합니다.
- 고유 대표 호가는 온라인 판매자 중복을 제거한 저가 10개 중앙값입니다. 수집할 수 있는 표본이 부족하면 적은 표본으로 계산할 수 있습니다.
- 옵션 범위가 유사한 저장 매물을 재활용합니다. 충분히 읽힌 극단 옵션은 필요할 때 별도 검색합니다. 비교 표본이 부족하면 대표 호가를 유지합니다.
- 조회 중·제한 대기·자료 없음·실패를 구분하고, 서버가 지정한 재시도 시간을 지킵니다.
- 가격 색상, 추정 표시, 수량 미확인 표시, 툴팁의 공물 1,000점당 가치 표시.
- Alt+Tab 복귀 시 화면을 다시 확인합니다. 이전 화면의 늦은 조회 결과를 새 화면에 적용하지 않습니다.

호가는 실제 체결 가격이 아닙니다. 자동 구매·보류·재추첨 및 자동 컨트롤러 순회는 제공하지 않습니다.

## 로컬 데이터와 개인정보

게임 화면은 외부 인식 서버로 전송하지 않습니다. 가격 조회 시 리그와 아이템·옵션 검색 조건은 해당 시세 서비스로 전달됩니다.

- 분석 원본과 진단 자료: EXE 옆 `captures/`에 자동 저장. 별도 오버레이 창은 캡처에서 제외합니다. **화면에 나타난 캐릭터명이나 대화 등이 포함될 수 있으므로 공개 업로드 전에 직접 확인하세요.** 자동 삭제하지 않습니다.
- 인식 자료: `%LOCALAPPDATA%/RitualChecker/assets/<자료 해시>/`에 첫 실행 시 자동 전개.
- 설정: `%LOCALAPPDATA%/RitualChecker/settings.json`.
- 시세 DB와 서버 대기 기록: `%LOCALAPPDATA%/RitualChecker/`.
- 수동 이름 수정 참조와 진단 시세 자료는 해당 인식 자료 폴더의 `data/`에 저장합니다. 자료 버전이 바뀌면 개인 참조는 자동 이전하지 않습니다.

다운로드는 EXE 하나지만 실행 시 런타임의 네이티브 라이브러리와 인식 자료가 로컬에 전개됩니다. 기존 폴더 배포 버전의 리그/입력 설정은 새 위치로 자동 이전되지 않습니다.

## 소스에서 빌드

Windows, .NET 8 SDK, Python 3.13을 준비합니다. 공개 저장소에 고정된 인식 자료가 있으므로 최초 빌드에 데이터 사이트 재수집은 필요 없습니다.

```powershell
python -m pip install -r tools/requirements.txt
./build.ps1
python -m pytest tests/test_tools.py -q
./build.ps1 -Publish -Version 0.1.1
```

결과: `artifacts/0.1.1/RitualChecker-0.1.1-win-x64.exe`와 `SHA256SUMS.txt`.
WPF 호환성을 위해 trimming은 사용하지 않습니다. 게시 단계는 단일 EXE 외 파일이 생기면 실패합니다.

참조 갱신은 `python tools/update_catalog.py --out data`로 수행합니다. 원격 이미지·옵션 변경은 인식 결과에 영향을 주므로 변경 자료는 다시 검증해야 합니다. 게임 이미지와 정보의 권리는 [별도 출처 안내](THIRD_PARTY_NOTICES.md)를 확인하세요.

## 검증과 한계

개발 캡처 38장(서로 다른 이미지 37장), 아이템 관측 526건, 툴팁 화면 7장에 대해 정의한 회귀 검사를 통과했습니다. 같은 화면의 반복 관측과 개발에 사용한 자료를 포함합니다. 독립 최종 시험이나 99.9999% 정확도 입증이 아닙니다.

원본 캡처와 정답 목록은 개인정보 때문에 공개하지 않습니다. 따라서 공개 CI의 단위 검사가 전체 캡처 회귀 검사를 재현하지는 않습니다. 평가 형식과 도구는 공개합니다. [검증 범위](docs/VALIDATION.md), [릴리스 절차](docs/RELEASING.md), [공개 전 점검](docs/PUBLICATION_REVIEW.md)을 참고하세요.

실제 HDR, 전체 해상도/창 모드, 모든 보류·흑백 상태, 독립 1,000장·10,000개 아이템 시험, 게임 프레임타임 영향과 전체 첫 가격 표시 지연 목표는 미검증입니다.

## 라이선스 및 출처

프로젝트 소스는 [MIT](LICENSE)입니다. 게임 아트·텍스트와 외부 의존성에는 각 권리자의 라이선스가 적용됩니다.

[PoE2DB](https://poe2db.tw/) · [PoE2Scout](https://poe2scout.com/) · [시세 연동 참고 프로젝트](https://github.com/Dev-Jahn/poe2-gpt)

This product isn't affiliated with or endorsed by Grinding Gear Games in any way.
