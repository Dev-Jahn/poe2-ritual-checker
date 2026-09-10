# Release procedure

Windows x64 개발 버전은 Semantic Versioning `0.MINOR.PATCH`를 사용합니다. 기능 묶음은 MINOR, 호환되는 수정은 PATCH를 올립니다. 안정화 전 GitHub Release는 prerelease로 게시합니다.

1. 공개 파일과 참조 데이터 변경을 검토합니다. `python tools/check_publication.py`를 실행합니다.
2. `src/Ritual.App/Ritual.App.csproj`의 Version과 `docs/releases/<version>.md`를 갱신합니다.
3. `./build.ps1 -Publish -Version <version>`과 Python 테스트를 실행합니다. 인식 변경이 있으면 비공개 캡처 회귀 검사를 다시 실행합니다.
4. 소스 폴더 밖에 EXE만 복사하여 최초 실행/자료 전개/PNG 재현을 확인합니다.
5. main에 커밋한 뒤 주석 태그 `v<version>`을 푸시합니다.
6. Windows release workflow가 다시 빌드·검사하고 EXE, SHA256SUMS.txt와 빌드 출처 증명을 게시합니다. 실패 시 릴리스 자산을 수동으로 바꿔 성공한 것으로 취급하지 않습니다.

이미 게시한 태그나 바이너리는 덮어쓰지 않습니다. 수정은 새 PATCH 버전으로 배포합니다. 현재 Authenticode 인증서는 구성하지 않았습니다.
