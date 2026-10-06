# 設計メモ

Any Dictation の内部設計、認識サービスとの通信の形、テストの範囲をまとめる。使い方は [README](../README.md) を参照。

## 構成

```text
src/AnyDictation.Core     フック状態機械、セッション状態機械、貼り付け判断、設定と履歴、認識クライアント、資格情報（UI 非依存）
src/AnyDictation.App      WPF アプリ本体（低レベルフック、録音、状態表示、設定画面、トレイ、コントローラー）
tests/AnyDictation.Tests  Core の単体テストと通信の統合テスト（xUnit）
tests/AnyDictation.E2E    実際の WPF アプリを操作する E2E（FlaUI.UIA3、明示実行のみ）
```

Native と WPF に依存する判断は、できるだけ Core の純粋なロジックに寄せ、fake（`ICredentialStore`、`IDeliveryEnvironment`、`CaptureSession` へ流すデバイス通知）で検証する。

## 認識サービスとの通信

認識は選択中のプロファイル 1 つにだけ送る。失敗しても別サービスへの fallback や自動 retry はしない。リダイレクトは追従しない（リダイレクト先へキーを送らないため）。

| 種別 | リクエスト | 認証 | 結果 |
| --- | --- | --- | --- |
| Azure MAI | `POST {endpoint}/speechtotext/transcriptions:transcribe?api-version=2025-10-15`。multipart の `audio`（WAV）と `definition`（`locales`、`enhancedMode.model`、`transcribeStyle: clean`） | `Ocp-Apim-Subscription-Key` | `combinedPhrases[].text` |
| Azure OpenAI | `POST {endpoint}/openai/deployments/{デプロイ名}/audio/transcriptions?api-version=2025-03-01-preview`。デプロイ名は URL の 1 区間としてエスケープする | `api-key` | `text` |
| OpenAI / 互換 | `POST {baseURL}/audio/transcriptions`（`file`、`model`、`language`、`response_format=json`） | `Authorization: Bearer` | `text` |
| Azure OpenAI Live | 下の節 | `api-key` | `completed` の全文 |

エンドポイントは HTTPS のみ。HTTP は loopback（`localhost`、`127.0.0.1`、`[::1]`）だけ許可する。OpenAI 互換の loopback に限り、キーが空でも送れる（ローカルの互換サーバー向け）。Azure の接続先は利用者ごとのリソースなので既定値を持たず、設定画面には入力例だけを表示する。

### Azure OpenAI Live

- 接続先は `wss://<リソース>/openai/v1/realtime?intent=transcription`。クエリに model、api-version、キーを置かない。
- 接続後に `session.update`（`type=transcription`、24 kHz の `audio/pcm`、`transcription.model` はデプロイ名、言語があれば `languages`、`turn_detection: null`）を送り、`session.updated` を受けてから `input_audio_buffer.append` を 100 ms ごとに送る。接続待ちの間の音声は順にためて送る。
- 停止すると最後のマイクバッファまで送り切り、`input_audio_buffer.commit` を 1 回送る。commit した項目の `item_id` と一致する `completed` の全文だけを結果にする。`delta` は途中表示だけに使い、貼り付けにも履歴にも入れない。
- `committed` に `item_id` が無い、または空の場合は失敗にする。どの項目が自分の結果か決められず、別の項目の全文を貼り付け得るため。
- `error`、文字起こしの `failed`、不正な JSON、バイナリ、切断、タイムアウトは失敗にし、音声を保持して手動の再送を待つ。自動再接続はしない。
- 時間制限は、接続と `session.updated` まで 15 秒、停止から結果まで 90 秒（録音中は数えない）。

## 主な設計判断

### キーの保存

- API キーは Windows 資格情報マネージャー（`AnyDictation/credential/<資格情報ID>`）にだけ保存する。設定 JSON には秘密ではない資格情報 ID だけを持つ。
- 保存では、キーを必ず新しい資格情報 ID へ書き、設定 JSON の原子的な保存が成功したときにだけ参照を切り替える。古いキーの削除は保存の成功後に行う。途中で失敗しても再起動しても、設定が参照するのは保存前のエンドポイントとキーの組のままになる。元へ戻す処理には依存しない。
- 失敗時に書いた未使用のキーや、削除できなかった古いキーは件数を表示する。

### 録音の停止と保持

- 停止操作では、マイクが最後のバッファを渡して終了を通知するまで待ってから録音を確定する。待機は async で、UI スレッドを塞がない。
- 停止操作なしに録音が止まった場合（マイクの切断など）は、音声を保持した未送信の再送待ちにする。自動送信も自動破棄もしない。
- 停止要求は録音ごとの `CaptureSession` に持たせる。取消の後に新しい録音を始めても、古い録音の遅れた停止通知が新しい録音を止めないようにするため。

### Live の送信

- 送信キューは上限なしの `Channel`（5 分で約 14 MB）。音声を落とさないことを優先し、上限や間引きは入れない。音声は `CaptureSession` にも全部残し、失敗後の再送に使う。
- 録音のコールバックは、音声のコピーを `Channel` へ `TryWrite` するだけにする（ブロックしない）。送信ループは 1 つだけ（WebSocket は同時送信できない）。受信ループは別に持つ。
- Live の接続は録音開始時に決まる。録音中に設定でプロファイルを替えても、その録音は開始時の接続で完結する。手動の再送だけが、再送時点の選択を使う。
- 16 kHz で保持した音声は Live へ送らない。リサンプラーは入れず、Live 以外のプロファイルでの再送を案内する。
- `ITranscriptionClient` は変更しない。Live は録音中の接続を持つため、バッチの `TranscribeAsync(WAV)` とは契約が違う。テスト用の差し替えは、`ClientWebSocket` の接続関数とループバックのサーバーで足りる。

### 貼り付けの判断

- 録音終了時の前面ウィンドウを記録し、認識完了時にもそのウィンドウが前面で、途中で一度も別ウィンドウへ移っていなければ Ctrl+V を送る。それ以外はクリップボードへのコピーだけにする。
- 前面ウィンドウの監視（`SetWinEventHook`）を開始できない場合、権限を判定する API が失敗した場合、貼り付け先が管理者権限の場合は、コピーだけにして理由を表示する。判定の失敗を「昇格していない」とは扱わない。
- 認識が成功した後の貼り付けの失敗は、API の再送状態にしない（課金の重複を防ぐため）。

## 配布と更新

- インストール版は Velopack で作る。per-user で管理者権限が要らず、通常のデスクトップアプリのまま低レベルフック、`SendInput`、HKCU Run の自動起動を使えるため。MSIX は更新で実行パスが変わり、Run キーも仮想化されるので使わない。
- `packId` は `diffshare.AnyDictation` とする。Velopack は `%LOCALAPPDATA%\{packId}` にインストールし、アンインストールでそのフォルダを丸ごと消す。`AnyDictation` にすると設定・履歴・ログの保存先と重なる。
- 本体は `%LOCALAPPDATA%\diffshare.AnyDictation\current\AnyDictation.exe` にあり、更新しても同じパスのまま中身だけが替わる。そのため、Run に `Environment.ProcessPath` を書く既存の自動起動は変えない。アンインストール時は、この exe を指す Run の値だけを消す（portable 版を指す値は残す）。
- 更新は `AppUpdater` が GitHub Releases を起動時と 24 時間ごとに確認し、裏でダウンロードする。適用は終了時か「再起動して更新」のときだけで、録音中、認識中、再送待ちの間は「再起動して更新」を受け付けない。通信の失敗はログに残し、利用者には通知しない。終了時は、ダウンロードを中止して終わるまで待つ（最後に更新プログラムを書き換えるため）。確認の通信中なら待たない。
- Velopack の起動時の自動適用は切る。二重起動（設定画面を出すための起動を含む）のたびに動き、起動中のインスタンスを止めてしまうため。代わりに単一インスタンスの Mutex を取った後、ダウンロード済みの更新があれば適用して再起動する。サインアウトなどで終了時に適用できなかった更新もここで適用する。更新直後の再起動では、適用に失敗したときの再起動の繰り返しを避けるため行わない。
- portable 版（Velopack でインストールしていない exe）と E2E では、更新の確認をしない。
- リリースは `vX.Y.Z` のタグの push で `.github/workflows/release.yml` が作る。vpk が必ず作る Portable.zip は自動更新する portable になり、自動更新しない portable 版と紛らわしいため、Releases には載せない。
- delta パッケージは作らない（`vpk pack --delta None`）。前のリリースの取得が不要で手順が単純になり、Releases には Setup.exe と full nupkg だけが載る。

## キーボードフック

### 方式

- Ctrl+Win の検出は低レベルキーボードフック（`WH_KEYBOARD_LL`）で行う。`RegisterHotKey` は修飾キーだけの組み合わせに使えないため。
- 発火は「Ctrl と Win が揃った後、他のキーを挟まずにどちらかを離した時点」。Ctrl+Win+矢印のような既存のショートカットでは発火しない。
- Win メニューを抑制するため、Ctrl と Win が揃った時点で未割り当てキー（VK 0xE8）を 1 回 `SendInput` する。自分が送ったイベントは `dwExtraInfo` の印で識別して無視する。

### フックを専用スレッドに置く理由

旧実装はフックを UI スレッドに置いていた。Windows は、低レベルフックのコールバックが `LowLevelHooksTimeout` 内に返らないと、そのイベントでフックを呼ばなくなり、通知なしでフックを外すことがある。そのため、UI が止まるとホットキーの監視も止まり得た。実際に、トレイからは録音できるのに Ctrl+Win だけが反応しなくなる事象があった（直接の原因は確定していない）。公式資料（`LowLevelKeyboardProc`）も、フックを専用スレッドに置くことを勧めている。

- `KeyboardHook` はメッセージループ付きの専用スレッドで動く。`HotkeyDetector` はそのスレッドだけが触る。`Toggled` はそのスレッドで同期的に呼ばれ、`Program` は UI スレッドへ `BeginInvoke` するだけにする。
- `Reset()` はどのスレッドからでも呼べる（`PostThreadMessage` でフックスレッドへ渡す）。
- `Install()` は起動の成否が分かるまで待ち、失敗は例外で呼び出し元へ返す。
- `Dispose()` は `WM_QUIT` を送り、スレッドの終了（フックの解除）まで最大 2 秒待つ。
- ログには `keyboard hook started` / `stopped` と `toggle requested source=hotkey ui_delay_ms=N` を書く。再発したときに、入力が届いていないのか UI が遅いのかを切り分けるため。

### 既定マイクの表示

「Windows の既定のマイク」には、WinMM が選ぶ現在の既定デバイス名を併記する。`waveInMessage(WAVE_MAPPER, DRVM_MAPPER_PREFERRED_GET, ...)` はデバイスを開かずに番号を返す。表示だけで、保存値は「既定」のまま（録音は `WAVE_MAPPER`）。WinMM の `ProductName` は 31 文字で切れる。

## テストの範囲

### 単体テストと通信の統合テスト（`tests/AnyDictation.Tests`）

| 対象 | 内容 |
| --- | --- |
| ホットキー | 左右の Ctrl / Win の全組み合わせと押す順・離す順で 1 回だけ発火する。キーリピート、他のショートカット、Shift / Alt / AltGr の先押しでは発火しない |
| セッションと貼り付け | 状態機械（認識中・再送待ち中は新しい録音不可、取消、不正な遷移の拒否）、前面ウィンドウの判断、管理者権限、監視の開始失敗、無音判定、WAV ヘッダー |
| 認識クライアント | 各サービスの URL、ヘッダー、multipart、応答の解析、HTTP エラーの分類、リダイレクト非追従、キーがエラー文や本文に出ないこと、別サービスへ fallback しないこと |
| Live | ループバックの `HttpListener` 上の本物の WebSocket サーバーに、本番と同じ `ClientWebSocket` で接続する。接続の形、送信順と commit 1 回、`item_id` の照合、フレームの分割、切断、タイムアウト、取消、再送、ログに秘密が出ないこと |
| 録音 | 停止後に届く最後のバッファを含める、録音ごとの停止要求、遅れた停止通知を無視する、録音ごとのサンプルレート |
| 設定と履歴 | エンドポイントと言語コードの検証、既定プロファイル、壊れた設定・履歴の保護と退避、履歴の 20 件上限、キー保存の失敗と再起動への耐性 |

### 設定画面の E2E（`SettingsGuiTests`）

実際の WPF アプリを専用モードで別プロセス起動し、UIA のパターン操作だけで設定画面を操作する。キーボードとマウスの実入力は使わない。設定は `TEMP/AnyDictation.E2E/<GUID>` に隔離し、資格情報はメモリのみ、マイク・フック・トレイ・自動起動・クリップボード・外部送信は無効にする。

- プロファイルの保存と再起動後の復元、不正な単価の拒否
- 保存バーと各入力欄が、既定サイズと最小サイズ（680 x 480）で全タブから到達できること
- 使用先の「保存済み」と「保存後」の表示、保存しないで閉じたときの破棄
- エンドポイントの入力例の表示
- 壊れた設定ファイルの保護、不正な隔離パスと二重起動の拒否

### 実フックの E2E（`KeyboardHookTests`）

UI スレッドがブロックに入ったことを待ってから Ctrl+Win を注入し、ブロック中も検出できること、ブロックが解けた後も検出できること（フックが外れていないこと）、`Reset` を別スレッドから呼ぶと保持中のキー状態が消えることを確認する。`Dispose` のテストはキーを注入せず、ログの `started` と `stopped` でスレッドの終了を確認する。

注入するキーには、本番のアプリが無視する印を付ける。ただし Windows と前面のアプリは Ctrl+Win を受け取るので、操作していないときに実行する。実行前に物理の修飾キーが離れていることを確認し、終了時に Ctrl / Win を解放する。

```powershell
$env:ANYDICTATION_RUN_HOOK_E2E = '1'
try { dotnet test tests/AnyDictation.E2E -c Release --filter "FullyQualifiedName~KeyboardHookTests" --logger 'console;verbosity=normal' }
finally { Remove-Item Env:ANYDICTATION_RUN_HOOK_E2E -ErrorAction SilentlyContinue }
```

### 自動テストの対象外

実マイクでの録音、実際の認識サービスへの接続（課金が発生する）、録音から認識・貼り付けまでの全経路、状態表示の実画面、自動起動の実操作、インストール・更新・アンインストールの実操作。
