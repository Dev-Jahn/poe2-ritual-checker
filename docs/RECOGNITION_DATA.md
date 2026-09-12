# 인식 자료 구축

실제 화면의 아이템 그림을 공식 참조에 보완하는 작은 특징 모델을 사용합니다. 게임 실행 중에는 CPU에서 로컬 자료만 비교합니다. 카탈로그 버전이 다른 모델은 읽지 않습니다.

## 화면 수집과 오인식 기록

분석 원본은 EXE 옆 `captures/`에 저장됩니다. **오인식 저장**을 누르면 `recognition-reports/`에 별도 기록을 만듭니다. 이름을 직접 수정할 때도 수정 전 기록을 저장합니다.

동일 화면으로 판단한 연속 분석에서 이름이나 크기가 달라지면 충돌 기록을 자동 저장합니다. 모든 오인식을 자동으로 알아내는 기능은 아니므로, 눈으로 발견한 문제는 버튼으로 남기세요. 같은 충돌의 반복 저장은 억제합니다.

각 기록에는 다음이 들어 있습니다.

- `capture.png`: 분석에 사용한 게임 원본.
- `capture.json`: 원본 해시, 앱·카탈로그·모델 버전, 위치·후보 점수, 원래 판독과 화면 표시 결과, 가격·툴팁 정보, 신고 또는 충돌 사유.
- `previous.png`: 자동 충돌을 비교한 이전 화면. 해당하는 경우만 저장합니다.
- `observed.png`: 수동 신고 시 분석 이후 관측한 화면. 분석 원본보다 최신일 수 있으며 별도로 구분합니다.

화면은 기존 게임 캡처 버퍼에서 가져오므로 앱 오버레이가 들어가지 않습니다. 저장 과정에서 화면을 외부로 보내거나 자동 삭제하지 않습니다. 전체 화면에는 개인정보가 포함될 수 있습니다. 원본과 정답 목록은 Git 및 릴리스 묶음에서 제외합니다.

## 중복 제거와 정답 확인

```powershell
python tools/capture_dataset.py --captures ./captures ./recognition-reports --out data/capture-dataset --materialize
```

PNG와 JSON의 해시를 확인하고, 의식 격자의 축소 영상·명암 주파수 특징으로 거의 같은 관측을 묶습니다. 격자 밖 게임 배경만 달라진 화면은 중복으로 취급합니다. 격자 위치가 없는 화면은 파일 해시로만 중복을 제거합니다. 묶인 원본과 당시 툴팁 기록은 `members`에 남깁니다. 이 자료는 아이템 그림 평가용이며, 격자 밖 툴팁 변화의 전수 시험용 자료가 아닙니다.

세션 단위로 학습·검증을 나눕니다. 동일 관측이 서로 다른 세션에 있으면 그 세션들을 같은 분할에 묶습니다. 다시 수집했을 때 이미 검토한 자료의 분할이 바뀌면 재검토하도록 중단합니다. `--materialize`는 확인한 원본을 자료 폴더에 하드 링크로 보관하며, 다른 볼륨에서는 복사합니다.

앱의 `prediction`은 정답이 아닙니다. 실제 그림과 이름을 확인한 뒤 해당 레코드에 `verifiedBy`와 `groundTruth`를 작성합니다. 전체 화면의 정답을 확인했다면 `complete: true`, 일부 아이템만 확인했다면 `false`를 사용합니다.

```json
{
  "verifiedBy": "reviewer-and-date",
  "groundTruth": {
    "complete": false,
    "items": [{
      "instanceId": "0:0",
      "catalogId": "Omen_of_Dextral_Annulment",
      "columns": 1,
      "rows": 1,
      "quantity": 1,
      "bounds": { "x": 100, "y": 200, "width": 70, "height": 70 }
    }]
  }
}
```

위 좌표는 형식 예시입니다. 전체 정답에는 `grid`의 `x`, `y`, `width`, `height`도 필요합니다. 창이 닫힌 정답은 `grid: null`, `items: []`입니다. 아이템 식별뿐 아니라 누락·분할·합치기·위치와 수량도 확인합니다. 신고, 후보 충돌, 사용자의 이름 수정만으로는 검증된 정답이 되지 않습니다.

확인창 때문에 가격 표시를 억제해야 하는 화면에서 가리지 않은 아이템 조각을 학습용으로 확인할 수도 있습니다. 이때는 `groundTruth.analysisSuppressed: true`로 명시합니다. 조각은 학습에 사용하지만 화면 분석의 기대 결과는 아이템 0개이며, 평가 보고서는 학습 조각 수와 표시할 아이템 수를 구분합니다.

## 모델 생성과 평가

```powershell
python tools/train_recognition.py --manifest data/capture-dataset/manifest.json --out data/recognition-model.json
dotnet run --project src/Ritual.Cli -c Release -- analyze data/capture-dataset/manifest.json --data data --league "Forbidden Rites" --out work/replay --no-images
python tools/validate_recognition.py --manifest data/capture-dataset/manifest.json --predictions work/replay --model data/recognition-model.json --out work/validation.json
```

학습은 확인한 **train** 레코드만 사용합니다. 아이템마다 24×48 명암·윤곽·색 비율 특징을 저장하고, 거의 같은 참조는 합칩니다. 학습 참조끼리 구분이 불분명하면 공식 참조를 우선합니다. 출력 모델에는 원본 전체 화면이나 개인 경로가 들어가지 않습니다.

검증 도구는 원본 해시와 학습 세션 혼입을 검사합니다. 전체 정답 화면은 누락·추가 영역까지 평가하고, 부분 정답 화면은 확인한 항목만 평가합니다. 미확인 자료만으로 통과 결과를 내지 않습니다. `--split train` 결과는 학습 적합도이며 일반화 정확도가 아닙니다.

CLI의 `--no-learned`는 실제 화면 특징 모델을 제외한 비교, `--temporal`은 같은 디렉터리의 연속 화면에서 이전 판독 유지와 충돌 기록을 확인하는 용도입니다. 서로 다른 게임 세션을 한 디렉터리에 섞지 마세요.

## 시즌 후보 목록

`data/ritual-pool.json`은 적용 리그·제외 항목·확인 근거를 함께 기록합니다. 현재 타락의 징조 제외는 이번 시즌 출현하지 않는다는 사용자 확인에 근거합니다. 다른 리그에는 자동 적용하지 않으며, 시세가 없다는 이유만으로 아이템을 제외하지 않습니다. 시즌이 바뀌면 이 목록도 다시 확인해야 합니다.
