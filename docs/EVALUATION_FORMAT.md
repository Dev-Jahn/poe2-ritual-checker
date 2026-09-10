# 사람이 확인하는 평가 자료

`data/evaluation-manifest.json`의 `groundTruth`는 초기값 null입니다. 개발 스크린샷 예측을 시험 정답으로 복사해 합격 처리하지 않습니다. 사람이 이미지와 실제 게임 이름·수량·상태를 확인한 뒤 기록하세요.

아래는 형식 설명용 예시이며 실제 시험 근거가 아닙니다.

```json
{
  "file": "data/captures/session-a/frame.png",
  "sha256": "원본 파일의 실제 SHA-256",
  "session": "독립 수집 세션 식별자",
  "split": "test",
  "source": "real",
  "independent": true,
  "fullFrame": true,
  "resolutionClass": "1440p",
  "hdr": true,
  "windowMode": "borderless",
  "verifiedBy": "정답 확인자",
  "groundTruth": {
    "complete": true,
    "grid": {"x": 100, "y": 200, "width": 840, "height": 700},
    "items": [
      {"catalogId": "Divine_Orb", "quantity": 2,
       "deferred": false, "dimmed": false, "selected": false,
       "bounds": {"x": 100, "y": 200, "width": 70, "height": 70}}
    ],
    "tooltip": null
  }
}
```

툴팁이 있다면 `tooltip`에 `catalogId`, `purchaseTribute`, `deferTribute`, `mods`, `corrupted`를 기록합니다. `mods`의 키는 영문 옵션 템플릿에서 숫자를 `#`로 치환하고 공백·구두점을 제거한 값이며, 값은 영문 템플릿 순서의 숫자 배열입니다. 예: `"#tomaximumlife": [43]`.

한글의 `1초마다`처럼 번역 과정에서 추가된 고정 숫자는 영문 옵션 값으로 옮기지 않습니다. 보류 비용을 구매 비용에 넣지 않습니다. 가려져 보이지 않는 아이템은 미관측으로 구분해 별도 사례로 관리하세요.

같은 수집 세션은 한 분할에만 넣습니다. 같은 원본의 크기/색 변형을 테스트 표본으로 늘리지 않습니다. 평가 도구는 원본 해시, 중복, 세션 누출, 개별 필드 오류와 누락/추가 영역, 해상도/HDR/창 모드 조합을 확인합니다.
