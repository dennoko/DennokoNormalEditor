# Dennoko Normal Editor 要件定義書

## 1. 概要・目的

**Dennoko Normal Editor** は、VRChatのアバター改変向けに、Unityエディタ（シーンビュー）上でカスタム法線を**非破壊・直観的・最小限のクリック数とマウス移動**で調整できる拡張機能である。  
NDMF（Non-Destructive Modular Framework）のプレビュー機構と統合し、Playモードに入ることなくlilToon等のシェーディング（影・リムライト・MatCap）の変化をリアルタイムに確認しながら法線を編集できる。

### 解決する課題
* **顔・髪・衣装の陰影調整の手間:** BlenderとUnityを往復することなく、Unity上の実際のマテリアル・ライティング環境を見ながら「頬の影を少し逃がしたい」「前髪のハイライトを整えたい」といった微調整を即座に行いたい。
* **球面法線メッシュへの立体感の部分復元:** 顔全体に球面法線が転写されてのっぺりしすぎているメッシュに対し、スムーズな陰影を維持したまま「鼻筋」「唇」「目元」などの本来のポリゴン形状の法線情報を少しだけ（例：20%〜50%）戻したい。
* **別メッシュ間の継ぎ目（首の境界など）の法線割れ:** 頭（Face/Head）と体（Body）が別々の `SkinnedMeshRenderer` に分かれているアバターやキメラ改変において、首の継ぎ目で発生する法線の不連続（明暗の境界線）を、複数メッシュをまたいで自然になじませて消したい。

---

## 2. コアコンセプト

1. **`DennokoMeshEditor` と統一された説明書不要の操作感**
   * 「対象メッシュを選んで編集開始 → シーンビューで頂点をクリックして影響範囲（球）を選択 → その場でハンドル操作・ホイールで半径調整 → `Esc` で選択解除／終了」という、`DennokoMeshEditor` と完全に共通のメンタルモデルを採用する。
2. **シェーディングを邪魔しないローカル可視化**
   * メッシュ全体に法線ラインを描画して影を見えなくするのではなく、**ホバー中・選択中のブラシ半径内の頂点のみ**に法線ピンを表示する。さらにドラッグ操作中はピン表示を減衰させ、実際のトゥーン影の動きに集中できるようにする。
3. **複数メッシュ（頭＋体など）のシームレスな横断編集**
   * 1つのコンポーネントに複数の `Renderer` を登録でき、ブラシ半径の選択・傾け・なじませ（Smooth）・形状法線の復元がすべて**メッシュの境界を越えて1つの連続したメッシュであるかのように**動作する。
4. **高パフォーマンスかつシンプルな実装**
   * 疎なバイナリデータ構造（`byte[] blob`）による軽量なシリアライズと、ドラッグ中にNDMFパイプライン再構築を走らせないインプレース更新（`LiveEdits` + `OnFrame`）により、高頂点数アバターでも軽快に動作する。

---

## 3. UX・操作仕様

### 3.1 編集開始の導線と対象メッシュ登録
* **Hierarchyコンテキストメニュー:**
  * `GameObject/dennokoworks/Dennoko Normal Editor` を実行すると、選択中の `GameObject`（複数選択時はそれらすべての `Renderer`、たとえば `Body` と `Face` の両方）を編集対象（`edits` リスト）に登録し、**即座に編集セッションを開始**する。
* **Inspectorの対象管理:**
  * 常設の「対象を追加」スロットを備え、複数オブジェクトの同時ドラッグ＆ドロップに対応する。
  * コンポーネントを `Renderer` を持つオブジェクトに直接アタッチした場合は、`Reset()` で自身の `Renderer` を自動登録する。

### 3.2 シーンビューでの基本操作（ハイブリッド方式）
編集セッション中（`Tools.hidden = true`）は以下の操作で完結する：

1. **ホバー ＆ 頂点クリック選択:**
   * マウスカーソル下の表面頂点（遮蔽判定付き）をハイライトし、ブラシ円と圏内の法線ピンをプレビュー表示する。
   * 左クリックでその頂点を中心とする影響範囲（全登録 `Renderer` 対象、減衰カーブ・ミラー対応）を確定する。
2. **選択頂点上の常時3Dハンドル（「傾ける / Tilt」操作）:**
   * 選択頂点から現在の法線方向に少し浮かせた位置（法線ピンの先端相当）に **`PositionHandle`** を常時表示する。
   * ハンドルを掴んで動かすと、引っ張った方向へ影響範囲内の法線が減衰重み付きで傾く（ミラーON時は対称側も鏡像方向に傾く）。
   * **ホイールによる半径変更:** ハンドルを掴んでいる間（動かしていない状態も含む）にマウスホイールを回すと、その場でブラシ半径が拡縮し、影響範囲と変形結果がリアルタイムに再計算される。
3. **シーンビュー右下オーバーレイ（選択範囲への即時エフェクト適用）:**
   * 視線移動を最小限にするコンパクトなドラッグ移動可能パネルをシーンビュー右下に表示する。
   * **共通設定:** ブラシ半径スライダー、減衰タイプ（Smooth / Sphere / Root / Sharp / Linear / Constant）、ミラーON/OFFトグル。
   * **選択範囲エフェクト操作（頂点選択中にアクティブ）:**
     選択中の範囲に対して、以下の効果をスライダー（ドラッグ中は `0% → 100%` でリアルタイムプレビューし、マウスを離すと確定して1段のUndoを記録）またはワンクリックで即座に適用できる：
     * **なじませる（Smooth）:** 範囲内の法線を周囲の頂点（**別メッシュの境界頂点・UVシームを含む**）と平均化して滑らかにする。首の継ぎ目や黒ずみの解消に使用する。
     * **形状を戻す（Restore Shape）:** 球面法線などが転写されたメッシュに対し、**「スムーズさを維持した本来のポリゴン形状法線（Smooth Geometric Normal）」** を指定割合でブレンドする。鼻筋・唇・目元などの立体感を自然に戻すのに使用する。
     * **揃える（Flatten）:** 範囲内の法線を、選択した中心頂点の法線方向（または範囲内の平均方向）へ均一に揃え、アニメ調のフラットな陰影を作る。
     * **球面化（Spherize）:** 影響範囲の中心（または背後の球中心）から放射状に法線を広げて球面法線化する。
     * **元に戻す（Erase / Reset）:** 選択範囲内の編集デルタを `0`（未編集の元メッシュ法線）に向かって戻す。
4. **選択解除・終了:**
   * `Esc` キー：頂点選択中は選択解除、未選択時は編集セッションを終了する。別オブジェクトの選択やPlayモード移行時も自動で安全に終了する。

### 3.3 一括球面化・別メッシュからの法線転写（Inspector補助機能）
局所的なギズモ編集に加え、ベース作りのための転写機能をInspector（またはオーバーレイの展開メニュー）に備える：
* **ターゲット法線転写（Transfer Normals）:**
  * 転写元 `Renderer`（球体メッシュや別素体など）を指定し、最も近い表面位置（または頂点）のワールド法線を対象 `Renderer`（全体、または現在の選択範囲のみ）へ非破壊データ（`NormalEdit` のデルタ）として焼き込む。
* **基準球面化（Spherize from Transform / Bounds）:**
  * 基準 `Transform`（Headボーン等）やバウンディングボックス中心を基準に、対象へ球面法線を非破壊データとして適用する。
  * これにより「まず顔全体を球面法線化（または転写）し、そのままシーンビューで鼻筋や口元をクリックして『形状を戻す』で立体感を出す」という一連のワークフローが本ツール単体で完結する。

---

## 4. アルゴリズム要件（複数メッシュ横断・スムーズ形状法線）

### 4.1 複数メッシュ横断の空間結合グラフ（Cross-Mesh Weld & Adjacency）
頭と体などの別メッシュ間の継ぎ目や、同一メッシュ内のUVシーム・ハードエッジ分割で法線が割れるのを防ぐため、編集セッションの対象読み込み時に以下の構造を構築する：

1. **結合グループ（Weld Group）の構築:**
   * 同一メッシュ内でバインドポーズ座標が完全一致する頂点同士を結合する。
   * さらに、**登録された複数 `Renderer` 間で、スキニング後のワールド座標の距離が微小閾値（既定 `0.001m` ＝ `1mm`）以内にある境界頂点同士**を同一のクロス・メッシュ結合グループとしてリンクする。
2. **境界頂点の法線同期:**
   * 首の継ぎ目などで同一結合グループに属する頂点（頭の下端頂点と体の上端頂点）は、同じ位置にあるため同じブラシ重みを受け取り、「なじませる」「形状を戻す」「揃える」において**同一のワールド目標法線**へ向かって収束する。これにより別メッシュ間の法線段差が完全に消える。

### 4.2 スムーズ形状法線（`SmoothGeometricNormals`）の算出
「形状を戻す（Restore Shape）」モードで使用する、元のメッシュ形状に由来する滑らかな法線を以下の手順で算出しキャッシュする：

1. **結合グループ単位の面積重み付き面法線合算:**
   * 各 `Renderer` のすべての三角形についてワールド空間（またはバインドポーズ空間）の面法線ベクトル（外積＝面積比例の重み付き）を計算し、その頂点が属する結合グループへ加算して正規化する。
   * クロス・メッシュ結合グループ（首の継ぎ目等）では、**頭側の三角形と体側の三角形の両方の面法線が合算される**ため、境界でも折れ曲がらない連続した面法線が得られる。
2. **トポロジー隣接によるラプラシアンスムージング（スムーズ維持）:**
   * ポリゴン分割が粗い箇所（鼻先や口角など）でポリゴンの角張りがそのまま出ないよう、三角形エッジで接続された隣接結合グループ間で1〜2パスの軽いラプラシアン平均化（正規化付き）を行ったものを `SmoothGeometricNormals` として保持する。
3. **球面補間による部分復元:**
   * 強度 $t \in [0, 1]$ と頂点ごとのブラシ減衰重み $w \in [0, 1]$ に対し、変更前の法線 $N_{\text{snap}}$ からスムーズ形状法線 $N_{\text{smooth\_geo}}$ へ $\text{Normalize}(\text{Vector3.Slerp}(N_{\text{snap}}, N_{\text{smooth\_geo}}, t \cdot w))$ で補間する。

### 4.3 スキニング対応とローカル法線デルタへの変換
* シーンビュー上の操作・可視化・複数メッシュ間の合成はすべて**スキニング後のワールド空間**で行う（ボーンが回転していても見た目のまま直観的に操作できる）。
* 各頂点のスキニング行列 $M_{\text{skin}} = \sum w_i (\text{bone}_i.\text{localToWorldMatrix} \times \text{bindpose}_i)$ から回転変換を取り出し、ワールド目標法線 $N_{\text{world}}'$ をバインドポーズ（メッシュローカル）空間の単位法線 $N_{\text{local}}'$ へ逆変換する。
* 保存するデルタは $\Delta N_{\text{local}} = N_{\text{local}}' - N_{\text{local\_base}}$ とし、メッシュへの適用時は：
  * 法線：$N' = \text{Normalize}(N_{\text{local\_base}} + \Delta N_{\text{local}})$
  * 接線（Tangent）：元の接線ベクトル $T_{xyz}$ を新しい法線 $N'$ に対してグラム・シュミット直交化（$T'_{xyz} = \text{Normalize}(T_{xyz} - N'(N' \cdot T_{xyz}))$、$T'_w = T_w$）してnormal mapや異方性反射の破綻を防ぐ。

---

## 5. アーキテクチャ・保守性・パフォーマンス要件

`DennokoMeshEditor` の実証済みアーキテクチャを踏襲し、モジュール分割と責務をシンプルに保つ。

### 5.1 データ構造（`DenNormalEditor` / `NormalEdit`）
* **`NormalEdit` クラス:**
  * `Renderer target`、`int vertexCount`、`int count`、`byte[] blob`（1頂点16バイト：`int32 index` + `float32 dx, dy, dz`）、`int revision` を保持する。
  * 変更がない頂点（$\|\Delta N\|^2 \le 10^{-8}$）は保存しない疎な形式で、シーンYAMLの肥大化とPrefabオーバーライドの負荷を防ぐ。
  * `revision` により、`ComputeContext.Observe` が毎フレーム配列の中身を走査せずに $O(1)$ で変更検知できるようにする。

### 5.2 NDMF連携・高速ライブプレビュー
* **ビルドパス（`DenNormalEditorPlugin`）:**
  * `BuildPhase.Transforming` かつ `AfterPlugin("nadena.dev.modular-avatar")` で実行する。
  * 複数コンポーネントが同一 `Renderer` を対象としている場合はデルタを加算合成した上で正規化して適用し、`ObjectRegistry.RegisterReplacedObject` と `AssetSaver.SaveAsset` を行う。
  * ビルド完了時にコンポーネント自身を破棄する（`IEditorOnly` 対応）。
* **プレビューフィルタ（`DenNormalEditorPreviewFilter` / `Node`）:**
  * ドラッグ操作中はコンポーネントを書き換えず、`LiveEdits` 経由でプレビューノードへ未確定デルタを渡す。
  * プレビューノードの `OnFrame` で事前確保配列を用いて `mesh.SetNormals()` / `mesh.SetTangents()` をインプレース更新し、**ドラッグ中にNDMFパイプラインの再構築（メッシュ複製）を一切走らせない**。
  * マウスリリース時（`Commit`）にのみコンポーネントへ書き込み、Undo 1段として記録する。

### 5.3 ディレクトリ・クラス構成（シンプル設計）
```text
DennokoNormalEditor/
├── Docs/Impl/
│   ├── overview.md
│   └── requirements.md
├── Runtime/
│   ├── dennokoworks.DenNormalEditor.Runtime.asmdef
│   ├── DenNormalEditor.cs          # MonoBehaviour本体
│   ├── Data/NormalEdit.cs          # byte[] blobによる疎な法線デルタ保持
│   └── Enums/                      # FalloffType, MirrorAxis
├── Editor/
│   ├── dennokoworks.DenNormalEditor.Editor.asmdef
│   ├── Core/
│   │   ├── NormalDeltaApplier.cs   # 法線デルタ適用・接線直交化・シェイプキー出力
│   │   ├── SmoothNormalCalculator.cs # 複数メッシュ横断Weld・スムーズ形状法線・近傍構築
│   │   ├── NormalTransferUtil.cs   # 球面化・別メッシュからの法線転写
│   │   ├── DenNormalEditorBaker.cs # Meshアセットへのベイク書き出し
│   │   ├── DenNormalEditorUndo.cs  # Undoグループ管理（RegisterCompleteObjectUndo）
│   │   └── FalloffUtil.cs
│   ├── Preview/                    # NDMF Plugin, PreviewFilter, PreviewNode, LiveEdits, ProxyRegistry, UpstreamNormalsCache, DownstreamGuard
│   ├── Session/                    # EditSession (State, Picking, Occlusion, Manipulation, Overlay, Rendering)
│   ├── Inspector/                  # DenNormalEditorInspector, HierarchyContextClick, HierarchyHighlight
│   └── Localization/               # DenNormalEditorLocalization, dennormal_localization.json
└── Tests/Editor/
    ├── dennokoworks.DenNormalEditor.Editor.Tests.asmdef
    └── NormalEditorTests.cs        # 計算ロジック・複数メッシュ結合・シリアライズの単体テスト
```

---

## 6. 実装上参考にするべき既存ツール・参照先一覧

本ワークスペース（`Assets/dennokoworks/DennokoNormalEditor`）の実装にあたっては、以下の既存ツールの設計・実装パターンをベースとして踏襲する。

### 6.1 `DennokoMeshEditor`（相対パス: `../DennokoMeshEditor/`）
操作フロー・疎なバイナリデータ保持・NDMFプレビュー最適化・Undo管理の直接のベースとなるツール。

* **Runtime / データ構造**
  * [`../DennokoMeshEditor/Runtime/DenMeshEditor.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Runtime/DenMeshEditor.cs): コンポーネント定義、`IEditorOnly`、`Reset()` での自身の `Renderer` 自動登録。
  * [`../DennokoMeshEditor/Runtime/Data/MeshEdit.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Runtime/Data/MeshEdit.cs): `byte[] blob`（1頂点16バイト: `int32 index` + `float32 x,y,z`）と `int revision` による疎なデルタ保持。YAML肥大化防止と $O(1)$ の変更検知。
  * [`../DennokoMeshEditor/Runtime/Enums/FalloffType.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Runtime/Enums/FalloffType.cs), [`MirrorAxis.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Runtime/Enums/MirrorAxis.cs): 減衰カーブとミラー軸の定義。
* **Core / Undo・ベイク・デルタ適用**
  * [`../DennokoMeshEditor/Editor/Core/MeshDeltaApplier.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Core/MeshDeltaApplier.cs): プレビュー・ビルド・ベイクで共通使用する適用ロジック、`RestoreBuffer` によるゼロアロケーション復元、`GeneratedMeshTracker` によるドメインリロード時のリーク防止。
  * [`../DennokoMeshEditor/Editor/Core/DenMeshEditorUndo.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Core/DenMeshEditorUndo.cs): `Undo.RegisterCompleteObjectUndo` と `CollapseUndoOperations` を用いたPrefab Reconciliationフリーズ回避パターン。
  * [`../DennokoMeshEditor/Editor/Core/DenMeshEditorBaker.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Core/DenMeshEditorBaker.cs): `_edited` サフィックス付きのアセット書き出し処理。
* **NDMF連携・高速ライブプレビュー**
  * [`../DennokoMeshEditor/Editor/Preview/DenMeshEditorPlugin.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Preview/DenMeshEditorPlugin.cs): `BuildPhase.Transforming`（`AfterPlugin("nadena.dev.modular-avatar")`）でのビルド実行と `ObjectRegistry.RegisterReplacedObject`。
  * [`../DennokoMeshEditor/Editor/Preview/DenMeshEditorPreviewFilter.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Preview/DenMeshEditorPreviewFilter.cs), [`DenMeshEditorPreviewNode.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Preview/DenMeshEditorPreviewNode.cs): `RenderGroup.WithData` による $O(N^2)$ 回避、`OnFrame` でのインプレース更新、`Refresh` でのノード再利用。
  * [`../DennokoMeshEditor/Editor/Preview/LiveEdits.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Preview/LiveEdits.cs), [`EditState.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Preview/EditState.cs), [`ProxyRegistry.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Preview/ProxyRegistry.cs), [`DownstreamGuard.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Preview/DownstreamGuard.cs): ドラッグ中の未確定データ受け渡し、プロキシ解決、下流フィルタ上書き検出。
* **シーンビュー編集セッション・UI**
  * [`../DennokoMeshEditor/Editor/Session/EditSession.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Session/EditSession.cs) ほか（`State.cs`, `Picking.cs`, `Occlusion.cs`, `Deformation.cs`, `Overlay.cs`, `Rendering.cs`）: エディタライフサイクルフック、CSR三角形ハッシュグリッドによる高速遮蔽判定、スキニング行列計算（`SkinMatrix`）、右下追従・ドラッグ移動可能なオーバーレイUI。
  * [`../DennokoMeshEditor/Editor/Inspector/DenMeshEditorInspector.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Inspector/DenMeshEditorInspector.cs), [`HierarchyContextClick.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Inspector/HierarchyContextClick.cs), [`HierarchyHighlight.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/DennokoMeshEditor/Editor/Inspector/HierarchyHighlight.cs): 複数選択対応のHierarchyコンテキストメニュー、複数同時ドロップ対応の追加スロット。

### 6.2 `FixNormal`（相対パス: `../FixNormal/`）
法線・接線の数学的処理および単体テスト構成の参考となるツール。

* [`../FixNormal/Editor/NormalFixer.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/FixNormal/Editor/NormalFixer.cs):
  * 同一座標頂点の結合グループ構築（`BuildWeldGroups`）、三角形の面積重み付き面法線合算（`ComputeGroupNormals`）、Rodriguesの回転公式（`Rotate`）、接線（Tangent）のグラム・シュミット直交化および `w` 成分保持の実装パターン。
* [`../FixNormal/Tests/Editor/dennokoworks.FixNormal.Editor.Tests.asmdef`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/FixNormal/Tests/Editor/dennokoworks.FixNormal.Editor.Tests.asmdef), [`NormalFixerTests.cs`](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/Unity_extension_development/Assets/dennokoworks/FixNormal/Tests/Editor/NormalFixerTests.cs):
  * Editor TestRunner用のアセンブリ定義と法線計算のテスト構成。


