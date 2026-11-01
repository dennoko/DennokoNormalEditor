# Dennoko Normal Editor 実装設計書

`requirements.md` を実装可能な粒度まで具体化したもの。
方針は **KISS**：`DennokoMeshEditor`（以下 DME）で実証済みの仕組みはそのまま移植し、法線編集に不要な部分は削る。新規に書くのは「法線に固有の部分」だけにする。

---

## 0. 設計の基本方針

| 方針 | 内容 |
|---|---|
| 既存コードの流用 | データ形式・NDMF 連携・Undo・セッションのライフサイクルは DME から移植する（型名の置換＋法線向けの差し替えのみ）。 |
| 処理経路を 1 本にする | Tilt・オーバーレイの 4 エフェクトは、すべて同じ「**影響頂点リスト × 目標法線 × 強度 t**」の経路で処理する。 |
| 合成規則を 1 か所に置く | デルタの合成と適用は `GatherEditsInto` と `NormalDeltaApplier` にだけ書き、プレビュー・ビルド・ベイクの 3 経路で共有する。 |
| 計算はワールド、保存はローカル | 操作・可視化・メッシュ間の合成はスキニング後のワールド空間で行い、保存するのはメッシュローカル空間のデルタだけにする。 |
| 最適化は計測してから | ピッキングや範囲検索は総当たりで実装する。遅いと分かった箇所だけ DME の高速化コードを移植する（§10）。 |

### DME から移植しないもの

| DME の要素 | 理由 |
|---|---|
| `MeshEdit` の旧形式（v1）移行 | 新規ツールなので旧データが無い。 |
| `GatherResult.Single` の高速パス・`VertexRestoreBuffer` | 法線は全配列コピー（頂点数 × 12B）でも十分軽い。 |
| `UpstreamVertices` のサンプリング検査 | 上流が**法線を** in-place で書き換えることは稀。Source インスタンスが変わったときだけ読み直す。 |
| `Occlusion` グリッド・高速射影・`VertexPreview` | 表面へのレイキャスト（総当たり）で代用する。最初にヒットした面を採るので、遮蔽も同時に解決する。 |
| `PreviewStats` / `PreviewDiagnostics` / デバッグ比較 | 開発補助なので、必要になってから足す。 |
| シェイプキーとしてのベイク | 法線には不要。 |

---

## 1. ディレクトリ・ファイル構成

```text
DennokoNormalEditor/
├── Docs/Impl/
│   ├── overview.md
│   ├── requirements.md
│   └── design.md                         # 本書
├── Runtime/
│   ├── dennokoworks.DenNormalEditor.Runtime.asmdef
│   ├── DenNormalEditor.cs                # MonoBehaviour 本体
│   ├── Data/NormalEdit.cs                # 疎な法線デルタ（byte[] blob）
│   └── Enums/
│       ├── FalloffType.cs
│       └── MirrorAxis.cs
└── Editor/
    ├── dennokoworks.DenNormalEditor.Editor.asmdef
    ├── Core/
    │   ├── NormalDeltaApplier.cs         # デルタ適用・接線直交化・Mesh 入出力
    │   ├── SkinningUtil.cs               # スキニング行列・ローカル⇔ワールド変換
    │   ├── WeldGraph.cs                  # 複数メッシュ横断の結合グループ・隣接
    │   ├── SmoothNormalCalculator.cs     # スムーズ形状法線
    │   ├── FalloffUtil.cs
    │   ├── DenNormalEditorBaker.cs       # Mesh アセット書き出し
    │   └── DenNormalEditorUndo.cs        # DME からそのまま移植
    ├── Preview/
    │   ├── DenNormalEditorPlugin.cs
    │   ├── DenNormalEditorPreviewFilter.cs
    │   ├── DenNormalEditorPreviewNode.cs
    │   ├── EditState.cs                  # DME からそのまま移植（型名のみ置換）
    │   ├── LiveEdits.cs
    │   ├── ProxyRegistry.cs
    │   └── DownstreamGuard.cs            # 第 5 段階で DME から移植（§7.5）
    ├── Session/
    │   ├── EditSession.cs                # ライフサイクル・入力の振り分け
    │   ├── EditSession.State.cs          # TargetState・形状の読み込み
    │   ├── EditSession.Picking.cs        # レイキャスト・範囲検索
    │   ├── EditSession.Operation.cs      # 影響範囲・Tilt・エフェクト・確定
    │   ├── EditSession.Overlay.cs        # 右下パネル
    │   └── EditSession.Rendering.cs      # 法線ピン・ブラシ円
    ├── Inspector/
    │   ├── DenNormalEditorInspector.cs
    │   └── DenNormalEditorMenu.cs        # Hierarchy メニュー
    └── Localization/
        ├── DenNormalEditorLocalization.cs
        └── dennormal_localization.json
```

- 名前空間：`Dennokoworks.DenNormalEditor`（Runtime）と `Dennokoworks.DenNormalEditor.Editor`（Editor）。
- asmdef は DME の構成をそのまま使う。参照は `VRC.SDKBase`、`nadena.dev.ndmf`、`nadena.dev.ndmf.runtime`。versionDefine として `com.vrchat.base` → `DEN_NORMAL_EDITOR_VRCSDK` を定義する。
- 要件 5.3 との差分：`SmoothNormalCalculator` に入れる予定だった Weld と隣接の構築を `WeldGraph.cs` へ分けた。Smooth・Restore の両方が使うためである。スキニング計算は、セッションと Baker で共有するため `SkinningUtil.cs` へ切り出した。

---

## 2. Runtime データ構造

### 2.1 `DenNormalEditor`（MonoBehaviour）

```csharp
[DisallowMultipleComponent]
[AddComponentMenu("dennokoworks/Dennoko Normal Editor")]
public class DenNormalEditor : MonoBehaviour
#if DEN_NORMAL_EDITOR_VRCSDK
    , VRC.SDKBase.IEditorOnly
#endif
{
    public List<NormalEdit> edits = new List<NormalEdit>();

    [Range(0.001f, 0.5f)] public float brushRadius = 0.03f;
    public FalloffType falloff = FalloffType.Smooth;
    public bool mirror;
    public MirrorAxis mirrorAxis = MirrorAxis.X;

    void Reset();                       // 自身の SMR / MeshRenderer を edits へ自動登録（DME と同じ）
    public NormalEdit FindEdit(Renderer target);
}
```

### 2.2 `NormalEdit`

DME の `MeshEdit` をそのまま複製し、次の 2 点だけを変える。

1. 旧形式（`indices` / `deltas`）と `MigrateIfNeeded` を削除する。
2. 保存を省く閾値を `sqrMagnitude <= 0` から `sqrMagnitude <= 1e-8f` に変える（要件 5.1）。

```csharp
[Serializable]
public class NormalEdit
{
    const int Stride = 16;              // int32 index + float32 dx,dy,dz（リトルエンディアン）
    const float ZeroThresholdSq = 1e-8f;

    public Renderer target;
    public int vertexCount;             // 編集時の頂点数。不一致なら適用しない
    [SerializeField, HideInInspector] int count;
    [SerializeField, HideInInspector] byte[] blob;
    [SerializeField, HideInInspector] int revision;   // 書き込みのたびに ++

    public int Count { get; }
    public int Revision { get; }
    public bool HasEdits { get; }
    public int GetIndex(int i);
    public Vector3 GetDelta(int i);
    public void CopyTo(Dictionary<int, Vector3> dst);
    public void SetFrom(Dictionary<int, Vector3> src, int newVertexCount); // 閾値以下は捨てる・revision++
    public void Clear();
}
```

**デルタの定義**：`ΔN = N'_local − N_base`。どちらもメッシュローカル空間の単位ベクトルである。
適用時は `N' = normalize(N_base + ΣΔN)` とする。編集が 1 件だけなら `N'_local` がそのまま復元される。

### 2.3 `FalloffType` / `MirrorAxis`

```csharp
public enum FalloffType { Smooth, Sphere, Root, Sharp, Linear, Constant }
public enum MirrorAxis { X, Y, Z }      // アバタールートのローカル空間で解釈
```

`FalloffUtil.Weight(t, type)`：t は正規化距離（0 が中心、1 が半径の端）。`t >= 1` なら 0 を返す。`s = 1 − t` とおく。

| Type | 式 |
|---|---|
| Smooth | `s*s*(3-2s)` |
| Sphere | `sqrt(1 - t*t)` |
| Root | `sqrt(s)` |
| Sharp | `s*s` |
| Linear | `s` |
| Constant | `1` |

---

## 3. Core

### 3.1 `NormalDeltaApplier`

プレビュー・ビルド・ベイクの 3 経路が共有する唯一の適用ロジック。

```csharp
internal static class NormalDeltaApplier
{
    static Mesh GetSharedMesh(Renderer r);           // DME から移植
    static void SetSharedMesh(Renderer r, Mesh m);   // DME から移植

    // baseN / baseT を out 配列へ写し、deltas の頂点だけ書き換える。確保はしない。
    static void Apply(Vector3[] baseN, Vector4[] baseT /*nullable*/,
                      Dictionary<int, Vector3> deltas,
                      Vector3[] outN, Vector4[] outT /*nullable*/);

    // ビルド・ベイク用。source を複製して法線と接線を書き換えた新規 Mesh を返す。
    static Mesh CreateEdited(Mesh source, Dictionary<int, Vector3> deltas);
}
```

`Apply` の処理：

```
Array.Copy(baseN → outN), Array.Copy(baseT → outT)
foreach (i, d) in deltas:
    n = baseN[i] + d
    if n.sqrMagnitude < 1e-12: continue          // 打ち消し合った場合は元の法線を残す
    n = n.normalized;  outN[i] = n
    if outT != null:
        t = baseT[i].xyz
        t' = t - n * dot(n, t)
        if t'.sqrMagnitude > 1e-12: outT[i] = (t'.normalized, baseT[i].w)
```

- 法線を持たないメッシュ（`normals.Length == 0`）は対象外とし、警告を出す。
- 接線を持たないメッシュでは接線を処理しない（`outT == null`）。
- 頂点位置・バウンズ・ブレンドシェイプには一切触れない。

### 3.2 `SkinningUtil`

DME `EditSession.Deformation.cs` の `SkinMatrix`、`Accumulate`、`MeshToWorld` を static ユーティリティとして切り出す。

```csharp
internal static class SkinningUtil
{
    // M = Σ wi * (bones[i].localToWorldMatrix * bindposes[i])。MeshRenderer なら MeshToWorld
    static Matrix4x4 SkinMatrix(in SkinSource src, int index);
    static Vector3 LocalToWorldNormal(in Matrix4x4 m, Vector3 n)    => m.MultiplyVector(n).normalized;
    static Vector3 WorldToLocalNormal(in Matrix4x4 inv, Vector3 nw) => inv.MultiplyVector(nw).normalized;
}
// SkinSource = { Renderer, Transform[] bones, List<Matrix4x4> bindposes, List<BoneWeight> weights }
```

**簡略化の判断**：厳密な法線変換は `M^{-T}` を使う。ここでは `M` と `M^{-1}` の組で変換する。この組は往復で必ず元に戻る（`normalize(M⁻¹·normalize(M·n)) = n`）ため、編集を繰り返しても誤差が溜まらない。誤差が出るのはボーンに非一様スケールがある場合の「見た目の角度」だけで、許容範囲とする。

### 3.3 `WeldGraph`（要件 4.1）

登録されたすべての Renderer の頂点を「グローバル頂点番号」`g = Offset[target] + i` で一列に並べる。その上に、結合グループと、グループ間の隣接を CSR 形式で持つ。

```csharp
internal sealed class WeldGraph
{
    public int[] Offset;                     // target → グローバル開始位置
    public int[] GroupOf;                    // グローバル頂点 → グループ
    public int GroupCount;
    public int[] MemberStart, Members;       // グループ → 所属頂点（CSR）
    public int[] NeighborStart, Neighbors;   // グループ → 隣接グループ（CSR、重複なし）

    public static WeldGraph Build(IReadOnlyList<WeldInput> inputs, float crossMeshThreshold = 0.001f);
}
// WeldInput = { Vector3[] localVertices, int[] triangles, Vector3[] worldVertices }
```

`Build` の処理（Union-Find を 1 つ使う）：

1. **メッシュ内の結合**：`Dictionary<Vector3, int>` を使い、ローカル頂点座標が完全一致する頂点同士を union する（`Vector3.Equals` は厳密比較）。
2. **境界頂点の抽出**：各三角形の 3 辺を、1 の結果のグループ番号の組 `(min, max)` で数える。使用回数が 1 の辺に乗っている頂点を「境界頂点」とする。
3. **メッシュ間の結合**：境界頂点のワールド座標を、一辺 `threshold` のグリッドハッシュへ入れる。近傍 27 セルを走査し、**別の Renderer** に属し距離が `threshold` 以下の頂点同士を union する。
   - 対象を境界頂点に限るのは、衣装と素体のように重なっている面を誤って結合しないためである。
4. Union-Find の根に通し番号を振って `GroupOf` を作り、CSR の `Members` を作る。
5. **隣接**：全三角形の辺 `(GroupOf[a], GroupOf[b])` をグループの組として集め、`HashSet<long>` で重複を除いてから CSR の `Neighbors` を作る。

再構築のタイミング：セッション開始時、対象リストの変化時、いずれかの対象の Source メッシュ（または頂点数）が変化したとき。**ポーズが変わっても作り直さない**。結合はセッション開始時のポーズで決まる。

### 3.4 `SmoothNormalCalculator`（要件 4.2）

```csharp
// 戻り値：targets ごとの「ローカル空間のスムーズ形状法線」配列
internal static Vector3[][] Compute(WeldGraph graph, IReadOnlyList<WeldInput> inputs,
                                    Func<int /*target*/, int /*index*/, Matrix4x4> inverseSkin,
                                    int laplacianPasses = 2);
```

1. グループ単位の配列 `acc[GroupCount]` を用意する。全 Renderer の全三角形について、ワールド座標の `cross(b − a, c − a)`（面積に比例する重み）を、3 頂点それぞれのグループへ加算する。最後に正規化する。
   - メッシュ間の結合グループには、頭側の面と体側の面の両方が足し込まれる。
2. ラプラシアン平均を `laplacianPasses` 回行う：`n'[g] = normalize(n[g] + mean(n[h] for h in Neighbors(g)))`。毎回、別の配列へ書き出してから入れ替える。
3. 各頂点のワールド法線 `n[GroupOf[g]]` をローカル空間へ変換し、target ごとの配列として返す（`WorldToLocalNormal`）。

ローカル空間で持つのは、セッション中にボーンが動いても、使う時点のポーズでワールドへ戻せばそのまま使えるからである。

### 3.5 `DenNormalEditorUndo` / `DenNormalEditorBaker`

- Undo：DME の `DenMeshEditorUndo` をそのまま移植する（`BeginGroup` / `Apply` / `EndGroup` / `Collapse`）。
- Baker：各対象について `GatherEdits` → `CreateEdited` を行い、`<元メッシュ名>_normal.asset` を元メッシュと同じフォルダ（読み取り専用なら `Assets/`）へ保存する。保存したアセットを Renderer へ割り当てるかは確認ダイアログで選ばせる。**FBX は書き換えない。**

---

## 4. NDMF 連携（要件 5.2）

### 4.1 `DenNormalEditorPlugin`

```csharp
public override string QualifiedName => "dennokoworks.den-normal-editor";

InPhase(BuildPhase.Transforming)
    .AfterPlugin("nadena.dev.modular-avatar")
    .AfterPlugin("dennokoworks.den-mesh-editor")   // 頂点編集の後に法線編集を適用し、順序を決定的にする
    .Run("Apply Normal Edits", Execute)
    .PreviewingWith(new DenNormalEditorPreviewFilter());
```

`Execute` は DME の構造をそのまま使う。

1. アバター配下の全コンポーネントを集める。
2. 対象 Renderer ごとに 1 回だけ処理する。アバター外の Renderer は警告を出してスキップする。
3. `GatherEdits(components, target, mesh.vertexCount, skipped)` を実行する。頂点数の不一致は `skipped` 経由で警告する。
4. `CreateEdited` の後、`ObjectRegistry.RegisterReplacedObject` → `AssetSaver.SaveAsset` → `SetSharedMesh` の順に行う。
5. 最後に全コンポーネントを `DestroyImmediate` する。

### 4.2 合成規則：`GatherEditsInto`（DME から移植。Filter に置く）

- 同じ Renderer を対象とする全コンポーネントの `edits` を、コンポーネントの順 → edits の順に走査する。
- `vertexCount` が現在の頂点数と一致しない編集は除外する（ビルド時だけ理由を報告する）。
- `LiveEdits` に未確定データがある編集は、そちらで置き換える。
- デルタは**加算**する。正規化は `NormalDeltaApplier.Apply` が 1 回だけ行う。
- 戻り値は `bool`（適用するものがあるか）だけにする。DME にあった `Single` の分岐は削る。

### 4.3 `DenNormalEditorPreviewFilter`

DME の Filter から、次のものをそのまま移植する。

- `GetTargetGroups`：編集中のコンポーネントは空の対象も含める。グループには `WithData(List<DenNormalEditor>)` で所有コンポーネントを添付する。
- `ObserveShape`：対象集合と編集の有無だけを fingerprint として監視する。
- `ObserveEdits`：セッション中のコンポーネントは監視しない。それ以外は `EditsTracker` で Revision を O(1) で監視する。
- `Refresh`（`TryReuse`）：上流メッシュが変わっておらず、プロキシの対応も同じならノードを再利用する。

`ObserveDownstreamSync` は第 5 段階で `DownstreamGuard` と一緒に追加する（§7.5）。

### 4.4 `DenNormalEditorPreviewNode`

```csharp
sealed class Entry
{
    public Renderer Original, Proxy;
    public Mesh Source;                       // 上流の出力。基準法線の読み出し元
    public Mesh Generated;                    // 自前の複製。編集が無ければ null
    public Vector3[] BaseN, OutN;             // Source が変わったときだけ確保
    public Vector4[] BaseT, OutT;             // 接線が無ければ null
    public readonly Dictionary<int, Vector3> Merged = new();
    public List<EditState> Current = new(), Applied = new();
    public bool HasApplied, LoggedFailure;
}
```

`OnFrame(original, proxy)`：

```
ProxyRegistry.Report(original, proxy, entry.Source)
upstream = GetSharedMesh(proxy)
if upstream == null:                    → 破棄して return
if upstream != Source && upstream != Generated:
    Source = upstream; BaseN = Source.normals; BaseT = Source.tangents（長さ 0 なら null）
    OutN/OutT を確保; HasApplied = false; DestroyGenerated()
EditState.Collect(components, original, Current, includeLive: true)
if !HasApplied || !SequenceEqual(Applied, Current):
    try { Rebuild() } catch { 1 度だけ LogError }   // 例外を NDMF へ漏らさない（DME と同じ）
if Generated != null: SetSharedMesh(proxy, Generated)
```

`Rebuild()`：

```
if !GatherEditsInto(...): DestroyGenerated(); 反映済みにする; return
if Generated == null: Generated = Instantiate(Source); HideAndDontSave; MarkDynamic()
NormalDeltaApplier.Apply(BaseN, BaseT, Merged, OutN, OutT)
Generated.SetNormals(OutN); if OutT != null: Generated.SetTangents(OutT)
Applied ⇄ Current を入れ替え; HasApplied = true
```

- ドラッグ中は `LiveEdits` のスタンプが変わるだけなので、パイプラインは作り直されない。`Rebuild` が生成済みメッシュの法線と接線を上書きするだけになる（要件 5.2）。
- 生成メッシュの破棄漏れ対策は、DME の `GeneratedMeshTracker` を移植して使う。

### 4.5 `LiveEdits` / `ProxyRegistry`

- `LiveEdits`：DME から `Publish` / `GetStamp` / `TryGet` / `Clear` / `Invalidate` を移植する。`SyncedVersion` とそれを間引いて送る処理は第 5 段階で追加する。
- `ProxyRegistry`：DME のものに **Source メッシュの登録**を加える。セッションは、編集が適用される前の基準法線を Source から読む必要があるためである。

```csharp
internal static void Report(Renderer original, Renderer proxy, Mesh source);
internal static bool TryGet(Renderer original, out Renderer proxy, out Mesh source);
```

---

## 5. 編集セッション

### 5.1 ライフサイクル（`EditSession.cs`）

DME の `EditSession` から、次のものをそのまま移植する。

- `Begin(component)`、`End()`
- `ActiveComponent`（`PublishedValue`）
- `CanContinueEditing`：選択が外れた、Play モードへの移行、コンパイル、シーンを閉じた場合に終了する。
- `InstallLifecycleHooks`
- Undo 後の `_resyncPending` → `ResyncFromComponent`
- `OnEditorUpdate` による 0.1 秒ごとの再描画
- `Tools.hidden` の退避と復元
- Esc の振り分け：選択中なら選択を解除し、未選択ならセッションを終了する。

`OnSceneGui` の処理順：

```
Esc 処理 → (操作中でなければ) Refresh(false) → AddDefaultControl
→ DrawOverlay → HandleHover/Click → HandleRadiusScroll → HandleTiltHandle → DrawPins/Brush
```

### 5.2 対象の状態（`EditSession.State.cs`）

```csharp
sealed class TargetState
{
    public Renderer Original, Proxy;
    public NormalEdit Edit;
    public SkinSource Skin;                    // bones / bindposes / weights
    public Mesh Source;                        // ProxyRegistry から。無ければ Original.sharedMesh
    public Vector3[] LocalVertices;            // Source.vertices（Weld 用）
    public int[] Triangles;                    // 全サブメッシュを連結したもの
    public Vector3[] BaseLocalN;               // Source.normals（デルタの基準）
    public Vector3[] SmoothGeoLocal;           // §3.4 の結果
    public Vector3[] WorldVertices;            // BakeMesh(Proxy) → ワールド。0.1 秒ごとに更新
    public Bounds WorldBounds;                 // ピッキングで対象を早期に除外するため
    public Mesh BakeScratch;
    public readonly Dictionary<int, Vector3> Working = new();   // LiveEdits へ参照のまま公開する
    public readonly Dictionary<int, Vector3> Snapshot = new();  // 操作開始時の Working
    public bool Touched;
}
WeldGraph _graph;   // 全対象で 1 つ
```

`Refresh(force)`：DME の `Refresh` と `SyncTargetList` を簡略化して移植する。

- 0.1 秒ごとに `WorldVertices` を BakeMesh で更新する。ドラッグ中とスライダー操作中は更新しない。
- 対象リストか Source インスタンスが変わったら、`TargetState` を作り直し、続けて `_graph` と `SmoothGeoLocal` を作り直す。

現在の法線は次の式で求める（必要な頂点についてだけ計算する）。

```
CurrentLocal(t, i) = normalize(BaseLocalN[i] + Working.GetValueOrDefault(i))
CurrentWorld(t, i) = LocalToWorldNormal(SkinMatrix(t, i), CurrentLocal(t, i))
```

プロキシのメッシュには編集済みの法線が入っているので、**セッションはプロキシから法線を読まない**。位置だけを BakeMesh で読む。

### 5.3 ピッキング（`EditSession.Picking.cs`）

```csharp
bool TryRaycast(Ray ray, out TargetState target, out int vertex, out Vector3 hitPoint);
void CollectInRange(Vector3 center, float radius, List<(TargetState, int, float dist)> dst);
```

- **TryRaycast**：`WorldBounds` と交差する対象についてだけ、全三角形と Möller–Trumbore 判定を行う。裏面も判定する。最も近いヒットを採り、そのヒット三角形の 3 頂点のうち、ヒット点に最も近い頂点を返す。最初にヒットした面を採ることで遮蔽判定を兼ねる。
- **CollectInRange**：全対象の `WorldVertices` を総当たりして距離の 2 乗で判定する。

| イベント | 処理 |
|---|---|
| MouseMove（操作中でない） | `TryRaycast` → ホバー頂点・ホバー中心を更新 |
| MouseDown 左（`nearestControl == defaultControl`、Alt なし） | ヒットすれば `BeginSelection`。ヒットしなければ選択解除 |

### 5.4 影響範囲（`EditSession.Operation.cs`）

```csharp
struct Influence
{
    public TargetState Target; public int Index;
    public float Weight;        // 主側と鏡像側のうち大きい方の重み
    public bool Mirrored;       // 鏡像側の重みを採用したか
    public Matrix4x4 Skin, InverseSkin;
    public Vector3 SnapWorld;   // 操作開始時の現在法線（ワールド）
    public Vector3 TargetWorld; // エフェクトの目標法線（エフェクト操作時のみ）
}
List<Influence> _influences;
```

`BuildInfluences()`：

```
_influences.Clear()
foreach target, i:
    w  = Falloff(|p - center| / R)
    wm = mirrorActive ? Falloff(|p - mirrorCenter| / R) : 0
    if max(w, wm) <= 0: continue
    add { Weight = max(w,wm), Mirrored = wm > w, Skin, InverseSkin, SnapWorld = CurrentWorld }
```

- ミラーの中心は DME の `RecomputeMirrorCenter` をそのまま使う。中心がミラー軸のごく近くにあるときはミラーを無効にする。
- 鏡像変換：`R(v) = root.TransformDirection(Reflect(root.InverseTransformDirection(v), axis))`
- 結合グループに属する頂点同士は（メッシュをまたいでも）同じ位置にあるので、自然に同じ重みを受け取る。グループを特別扱いする処理は要らない（要件 4.1-2）。

**目標法線を書き込む共通処理**（Tilt・エフェクトのすべてがここを通る）：

```csharp
void WriteResult(in Influence inf, Vector3 resultWorld)
{
    var local = WorldToLocalNormal(inf.InverseSkin, resultWorld);
    var delta = local - inf.Target.BaseLocalN[inf.Index];
    var w = inf.Target.Working;
    if (delta.sqrMagnitude <= 1e-8f) w.Remove(inf.Index); else w[inf.Index] = delta;
    inf.Target.Touched = true;
}
void PublishWorking();     // Touched な target の Working を LiveEdits.Publish
```

範囲外になった頂点を元に戻すため、適用の直前には毎回 `Working ← Snapshot` に戻してから書き込む。DME の `ResetWorkingToSnapshot` と同じやり方で、半径をホイールで縮めた場合に効く。

### 5.5 Tilt（3D ハンドル）

- **ハンドル位置**：`_handlePos = centerPos + CurrentWorld(center) * pinLength`。`pinLength = HandleUtility.GetHandleSize(centerPos) * 0.8`（画面上の大きさが一定になる）。
- `Handles.PositionHandle(_handlePos, Tools.handleRotation)` を使う。掴んだこと（`_grabbed`）の検出、ドラッグ中の判定、`rawType == MouseUp` と `hotControl == 0` による確定は、DME の `HandleDrag` をそのまま移植する。
- 掴んだ瞬間に `Snapshot ← Working` とし、`BuildInfluences()` で `SnapWorld` と `_centerSnap` を固定する。
- 移動するたびに：

```
dir = normalize(_handlePos - centerPos)
q   = Quaternion.FromToRotation(_centerSnap, dir)
qm  = Quaternion.FromToRotation(R(_centerSnap), R(dir))
foreach inf:
    rot = Quaternion.Slerp(identity, inf.Mirrored ? qm : q, inf.Weight)
    WriteResult(inf, rot * inf.SnapWorld)
PublishWorking()
```

- **ホイール**：掴んでいる間（動かしていない間も含む）だけ横取りする。`R *= 1.05^notches` とし、`BuildInfluences()` → 上記の適用を再実行し、`current.Use()` する。半径は確定まで `_pendingRadius` に持つ（DME と同じ）。
- 離したら `Commit()` を呼び、`_handlePos` を新しい中心法線の位置へ置き直す。

### 5.6 選択範囲エフェクト（要件 3.2-3）

すべてのエフェクトは「操作開始時に影響頂点ごとの `TargetWorld` を 1 回だけ計算し、強度 t のたびに補間するだけ」という同じ形で処理する。

```csharp
enum Effect { Smooth, RestoreShape, Flatten, Erase }

void BeginEffect(Effect e)     // スライダーを掴んだ瞬間、またはボタンを押したとき
{
    Snapshot ← Working; BuildInfluences();
    foreach inf: inf.TargetWorld = ComputeTarget(e, inf);
}
void UpdateEffect(float t)     // 0..1。スライダーの値が変わるたびに呼ぶ
{
    Working ← Snapshot;
    foreach inf: WriteResult(inf, Vector3.Slerp(inf.SnapWorld, inf.TargetWorld, t * inf.Weight).normalized);
    PublishWorking();
}
void EndEffect() => Commit();  // スライダーを離したとき。スライダーは 0 に戻す
// ボタン：BeginEffect(e); UpdateEffect(1); EndEffect();
```

`ComputeTarget(e, inf)`（`g = GroupOf(inf)`）：

| Effect | 目標法線（ワールド） | 鏡像側（`Mirrored`） |
|---|---|---|
| Smooth | `normalize(GN(g) + mean(GN(h) for h in Neighbors(g)))` | 同じ（対称） |
| RestoreShape | `LocalToWorldNormal(Skin, SmoothGeoLocal[i])` | 同じ |
| Flatten | `_centerSnap`（選択中心頂点の法線） | `R(_centerSnap)` |
| Erase | `LocalToWorldNormal(Skin, BaseLocalN[i])` | 同じ |

- `GN(g)` はグループ法線で、`normalize(Σ CurrentWorld(m) for m in Members(g))`（Snapshot 基準）。`BeginEffect` の中で `Dictionary<int, Vector3>` にメモ化し、影響範囲のグループとその隣接だけを計算する。範囲外の隣接グループ（別メッシュ側も含む）も読むので、境界をまたいでなじむ（要件 3.2 Smooth）。
- 同じ結合グループの頂点は同じ目標と同じ重みを持つので、t = 1 で完全に一致する。これによりメッシュ間の段差が消える（要件 4.1-2）。
- Smooth はラプラシアン平均 1 パス分を目標にする。より強くなじませたいときはボタンを繰り返し押す（押すたびに Undo 1 段）。
- Restore の補間は要件 4.2-3 の `Slerp(N_snap, N_smooth_geo, t·w)` そのものである。
- Flatten の「範囲内の平均方向」はオプションにしない。中心頂点の法線だけにする（KISS）。
- Erase は、目標を元の法線にした Slerp として実装する。デルタを直接縮める特別な経路は作らない。

### 5.7 確定（Commit）

DME の `Commit` をそのまま移植する。

```
Touched な対象も _pendingRadius も無ければ LiveEdits を破棄して return（空の Undo を積まない）
DenNormalEditorUndo.BeginGroup(component, "Dennoko Normal Editor")
_pendingRadius を書き込む
Touched な各対象: Edit.SetFrom(Working, vertexCount)
DenNormalEditorUndo.Apply(component); EndGroup()
LiveEdits.Clear()（無ければ Invalidate）; Touched = false
```

### 5.8 描画（`EditSession.Rendering.cs`）

- **ブラシ円**：ホバー中心か選択中心に、その点の法線を軸とした `Handles.DrawWireDisc` を描く。ミラーが有効なら鏡像側にも描く。
- **法線ピン**：ホバー中か選択中のブラシ内にある頂点だけを描く（ホバーは `CollectInRange`、選択は `_influences`）。
  - 線分は `p → p + CurrentWorld * pinLength * 0.5`。色は重みに応じて変える（低い：青 → 高い：黄）。
  - 本数は最大 2000 本とし、超えたら等間隔に間引く。`Handles.DrawLines` でまとめて 1 回描く。
  - Tilt・エフェクトの操作中は α を 0.15 まで下げ、トゥーン影が見えるようにする（要件 2-2）。
- 選択中心の頂点はドットで強調する。

### 5.9 オーバーレイ（`EditSession.Overlay.cs`）

DME の `DrawOverlay`（`Handles.BeginGUI` + 自前の矩形）を土台にする。初期位置はシーンビューの右下で、ヘッダーをドラッグすると移動でき、位置はセッション中だけ保持する。

```
┌ Dennoko Normal Editor ───────────── [×] ┐
│ 半径   [====|------] 0.030               │
│ 減衰   [Smooth ▼]   ミラー [✓] [X ▼]     │
│ ─────────────────────────────────────── │  ← 頂点を選択している間だけ有効
│ [なじませる]   [|-----------] 0%          │
│ [形状を戻す]   [|-----------] 0%          │
│ [揃える]       [|-----------] 0%          │
│ [元に戻す]     [|-----------] 0%          │
└──────────────────────────────────────── ┘
```

- **スライダー**：`GUI.HorizontalSlider` を使う。`hotControl` が自分の ID になった瞬間に `BeginEffect`、値が変わるたびに `UpdateEffect`、`rawType == MouseUp` か hotControl の解放で `EndEffect` を呼ぶ。
- **ボタン**：t = 1 で即座に適用する。
- **共通設定**：半径・減衰・ミラーの変更は、DME と同じく `_settingsUndoGroup` で Undo 1 段にまとめ、`NotifySettingsChanged()` で影響範囲を計算し直す。
- パネル上では、クリックと MouseMove をシーンへ通さない（`_overlayRect.Contains`）。

---

## 6. Inspector / メニュー

### 6.1 Hierarchy メニュー（`DenNormalEditorMenu`）

`GameObject/dennokoworks/Dennoko Normal Editor` は、DME の `AddDenMeshEditorMenuItem` を移植して使う。

- 複数選択時にメニューが連続で呼ばれる問題には、`_menuHandled` と `delayCall` で対処する。
- 選択中のオブジェクトの `SkinnedMeshRenderer` と `MeshRenderer` をすべて集め、`edits` へ重複なく登録する。
- コンポーネントの付け先は DME の `ResolveMenuTarget` の規則に従う。
- 最後に `EditSession.Begin(component)` を呼び、**すぐに編集を始める**。

### 6.2 `DenNormalEditorInspector`

```
[ 編集開始 / 編集終了 ]                       ← 大きなトグルボタン
対象 ─────────────────────────────
  Body (SkinnedMeshRenderer)   1,234 頂点編集済  [クリア][×]
  Face (SkinnedMeshRenderer)     512 頂点編集済  [クリア][×]
  [ ここへドロップして追加 ]                  ← 複数同時ドロップに対応（DME から移植）
ブラシ ───────────────────────────
  半径 / 減衰 / ミラー / ミラー軸
ベイク ───────────────────────────
  [ Mesh アセットとして書き出し ]
```

### 6.3 ローカライズ

DME の `DenMeshEditorLocalization` と JSON の仕組みを移植し、キーを差し替える。対応言語は日本語と英語で、既定は日本語とする。

---

## 7. 処理の流れ（まとめ）

### 7.1 ドラッグ中（毎フレーム）

```
PositionHandle の移動
  → Working を更新（WriteResult）→ LiveEdits.Publish（スタンプ++）
  → PreviewNode.OnFrame：EditState が一致しない → Rebuild：Gather → Apply → SetNormals/SetTangents
     （パイプラインは作り直さない。メッシュも複製しない）
```

### 7.2 確定

```
MouseUp → Commit：NormalEdit.SetFrom（revision++）→ Undo 1 段 → LiveEdits.Clear
  → Node：Revision が変わったので 1 回 Rebuild（セッション中のコンポーネントは NDMF の監視対象外なので、パイプラインは作り直さない）
```

### 7.3 Undo / Redo

```
undoRedoPerformed → _resyncPending → 次の Update で ResyncFromComponent
  → Working/Snapshot を作り直し、選択を復元し、影響範囲を作り直す（DME と同じ）
```

### 7.4 セッション外

`ObserveEdits`（Revision の監視）がノードを無効化し、NDMF の通常の経路で更新される。

### 7.5 下流フィルタとの併用（第 5 段階）

AAO などの下流フィルタがメッシュを差し替える構成では、ノードが in-place で書き換えた内容が下流に届かない。DME の `DownstreamGuard`、`LiveEdits.SyncedVersion`、`ObserveDownstreamSync` を**そのまま移植**して対応する。第 4 段階までは既知の制限として扱うが、リリースまでに必ず入れる。

---

## 8. 定数一覧

| 定数 | 値 | 場所 |
|---|---|---|
| デルタを保存しない閾値（2 乗） | `1e-8` | `NormalEdit` / `WriteResult` |
| メッシュ間の結合距離 | `0.001 m` | `WeldGraph.Build` |
| ラプラシアンのパス数 | `2` | `SmoothNormalCalculator` |
| ブラシ半径の範囲 | `0.001 – 0.5` | `DenNormalEditor` |
| ホイール 1 目盛りあたりの倍率 | `1.05` | `EditSession` |
| 形状の更新間隔 | `0.1 s` | `EditSession.Refresh` |
| ピンの最大本数 | `2000` | `Rendering` |
| 操作中のピンの α | `0.15` | `Rendering` |
| ハンドル距離 | `GetHandleSize × 0.8` | `Operation` |

---

## 9. 実装順序

各段階が終わった時点で、動作確認ができる状態にする。

| 段階 | 内容 | 確認方法 |
|---|---|---|
| 1 | Runtime 一式、`NormalDeltaApplier`、`DenNormalEditorPlugin` | デバッグ用メニューで、全頂点に固定デルタを書き込む → Avatar をビルドして法線が変わること |
| 2 | Preview 一式（DownstreamGuard を除く） | 1 と同じデータがシーンビューに反映されること。Inspector でクリアすると戻ること |
| 3 | Session：対象の読み込み、レイキャスト、ピン、Tilt、Commit、Undo、Esc、Hierarchy メニュー | 顔の頂点を選んで傾け、Undo が 1 段で戻ること |
| 4 | `WeldGraph`、`SmoothNormalCalculator`、オーバーレイの 4 エフェクト | Body と Face の首の継ぎ目に「なじませる」をかけて段差が消えること。球面法線の顔に「形状を戻す」をかけて鼻筋が出ること |
| 5 | Baker、ローカライズ、`DownstreamGuard` | ベイクしたメッシュに編集が反映されること。AAO を併用してもプレビューされること |

---

## 10. 性能の見込みと、遅かったときの対処

| 処理 | 方式 | 見込み（頂点・三角形 10 万） | 遅かった場合 |
|---|---|---|---|
| ホバー時のレイキャスト | バウンズ判定 + 三角形の総当たり | 数 ms / MouseMove | DME の `Occlusion` のスクリーングリッドを移植する |
| ブラシの範囲検索 | 頂点の総当たり | < 1 ms | ワールド座標のグリッドハッシュにする（Refresh のたびに作り直す） |
| WeldGraph + スムーズ形状法線 | Union-Find + CSR | 100 ms 前後（セッション開始時の 1 回） | 結果を `Source` のインスタンス ID をキーにキャッシュする |
| ドラッグ中の Rebuild | 配列の全コピー + SetNormals/SetTangents | < 1 ms | DME の `VertexRestoreBuffer` のように、編集された頂点だけを差分で書く |
| 影響範囲の SkinMatrix | 影響頂点ごとに計算 | 影響頂点数に比例 | `BuildInfluences` のたびにキャッシュする（すでにしている） |

---

## 11. 既知の制限（仕様として許容するもの）

- ボーンに非一様スケールがある場合、ワールド法線の角度がわずかにずれる（§3.2）。往復は一貫しているので、誤差は溜まらない。
- 結合はセッション開始時のポーズで判定する。セッション中に大きくポーズを変えても結合は作り直さない。
- 上流のツールが法線を in-place で書き換える場合、その変化は Source インスタンスが変わるまでプレビューに反映されない。
- 頂点数か頂点順が変わったメッシュ（元メッシュの差し替えなど）では、編集を適用しない（ビルド時に警告する）。
