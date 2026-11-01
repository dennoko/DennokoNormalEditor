# 強度マスク 実装計画

編集対象（Renderer）ごとにマスク画像を 1 枚指定し、UV0 上の明るさで編集の反映強度を変える。
白は 100%、黒は 0%、その間は線形に補間する。マスクは Inspector で設定し、シーンのオーバーレイには UI を追加しない。

---

## 1. 方針（KISS）

- **マスクは対象ごとに持つ**：異なるメッシュが UV を共有することはまず無いため。マスクのデータは `NormalEdit`（1 Renderer 分の編集データ）に置く。
- **掛ける場所は 1 か所**：マスクは `BuildInfluences()` の中で `Weight` と `MirrorWeight` に掛ける。Tilt と 5 つのエフェクトは、どれもこの 2 つの重みだけを読んでいるので、個別の修正は要らない。
- **計算はキャッシュ時の 1 回だけ**：マスクの値は「頂点ごとの最終値 `float[]`」として、マスクか形状が変わったときにだけ作り直す。ドラッグ中にテクスチャを読むことはしない。
- **複数の UV に属する頂点**：既存の結合グループ（`CrossMeshTopology.VertexToGroup`）の中の**最小値**を、グループ全員に配る。
  - これで「黒に近い方を優先する」を満たす。
  - 結合グループはメッシュをまたぐので、首の継ぎ目では「頭のマスク」と「体のマスク」の小さい方が両側に効く。マスクが未指定の対象は 100% として扱う。
  - 同じグループの頂点は必ず同じ重みを受け取るので、UV シームや首の継ぎ目に法線の段差が生まれない（要件 4.1-2 の保証が崩れない）。
- **プレビューとビルドには関与しない**：マスクは操作の時点でデルタに焼き込まれる。デルタの形式も NDMF 側も変更しない。

---

## 2. 変更点

### 2.1 Runtime：`Data/NormalEdit.cs`

```csharp
[Tooltip("UV0 上の明るさで、この対象への編集の強度を調整します（白 = 100%、黒 = 0%）。シーン上の操作にだけ効きます。")]
public Texture2D strengthMask;
```

- シリアライズされるので、セッションを閉じても設定が残り、Undo の対象にもなる。
- `EditState` は `Revision`、`Count`、`vertexCount`、`LiveStamp` だけを比べ、`ShapeFingerprint` は `target` と `HasEdits` だけを見る。どちらもこのフィールドを見ないので、マスクを変えても NDMF のパイプラインは再構築されない。
- `SetFrom` と `Clear` には触らない。編集をクリアしてもマスクの指定は残る。

### 2.2 新規：`Editor/Core/StrengthMaskUtil.cs`（約 80 行）

```csharp
internal static class StrengthMaskUtil
{
    // 読み取り不可や圧縮形式のテクスチャでも読めるよう、RenderTexture へ Blit して ReadPixels する
    internal static bool TryRead(Texture2D tex, out Color32[] pixels, out int width, out int height);

    // バイリニア補間。wrapMode が Clamp なら端で止め、それ以外は繰り返す
    internal static float Sample(Color32[] px, int w, int h, Vector2 uv, TextureWrapMode wrap);

    // 頂点ごとのマスク値を計算する。UV0 が無いメッシュなら null を返す（= 100% 扱い）
    internal static float[] ComputeVertexMask(Mesh mesh, Color32[] px, int w, int h, TextureWrapMode wrap);
}
```

- **値の取り方**：明るさ（`Color.grayscale`）を使い、アルファは見ない。
- **色空間**：RenderTexture の読み書き設定を `tex.isDataSRGB` に合わせる。これでインポート設定にかかわらず、ペイントツールで見た値のまま（50% のグレーは 50%）になる。
- **解像度**：読み取りが重くならないよう、長辺が 1024px を超える場合は 1024px に縮小して Blit する。

### 2.3 新規：`Editor/Session/EditSession.Mask.cs`（約 80 行）

`TargetState` に追加するフィールド：

```csharp
public Texture2D MaskSource;      // キャッシュを作ったときのマスク
public Hash128 MaskHash;          // 外部での再インポートを検知するため（imageContentsHash）
public float[] VertexMask;        // 対象自身のマスク値（グループで最小値を取る前）
public float[] Mask;              // グループの最小値を取った後の最終値。null なら 100%
```

```csharp
private bool _maskDirty = true;

private void RebuildMaskIfNeeded();
private static float MaskAt(TargetState t, int i) => t.Mask == null ? 1f : t.Mask[i];
```

`RebuildMaskIfNeeded()` の処理：

1. 対象ごとに `Edit.strengthMask` と `MaskSource` / `MaskHash` を比べる。変わった対象だけ、`VertexMask` を作り直す。
   - UV は **`OriginalMesh` の UV0** から取る。マスクは元テクスチャの UV に合わせて描かれるので、上流のツール（アトラス化など）が UV を書き換えていても影響を受けない。頂点数が合わない場合は Proxy 側のメッシュを使う。
   - マスクが未指定なら `VertexMask = null` とする。
2. どれか 1 つでも作り直したか、トポロジーが作り直されていれば、グループの最小値を計算し直す。
   - `float[GroupCount]` を 1 で初期化し、全対象の `VertexMask` から最小値を集める。
   - 結果を各対象の `Mask[i]` へ書き戻す。
   - グループに 1 枚もマスクが掛かっていない対象は `Mask = null` のままにする（100% 扱いの高速パス）。

**呼び出し元**：

- `BuildInfluences()` の先頭：参照の比較だけなので軽い。Inspector で変更した直後も、`NotifySettingsChanged` → `BuildInfluences` の経路で即座に反映される。
- `Refresh` でトポロジーを作り直した直後：`_maskDirty = true` にする。
- `Refresh`（0.1 秒ごと）：`imageContentsHash` を比べ、外部での再インポートを検知する。
- `ResyncFromComponent`：Undo でマスクが変わった場合に対応する。

### 2.4 `EditSession.Manipulation.cs`：`BuildInfluences()`

```csharp
var mask = MaskAt(target, i);
if (mask <= 0f) continue;
weight *= mask;
mirrorWeight *= mask;
```

- 鏡像側では、**その頂点自身の** UV のマスクを使う。左右の UV が対称でないマスクでも、描いたとおりに効く。
- `Erase` は `Weight + MirrorWeight` を使い、`Flatten` はそれぞれの重みを使っているので、どれも追加の変更は要らない。

### 2.5 `EditSession.Rendering.cs`

- 選択中のピンは `_influences` の重みで色が決まるので、マスクは自動的に反映される。
- ホバー中のプレビュー（`DrawHoverGizmos`）は独自に減衰を計算しているので、`w *= MaskAt(target, i)` を 1 行足し、マスクが 0 の頂点はピンを描かない。
- オーバーレイに UI を足さなくても、ブラシを当てるとマスクの効き方がシーン上で見える。

### 2.6 `DenNormalEditorInspector.cs`：対象リストの各行

各対象の行の下に、インデントした 1 行を追加する。

```
対象 ─────────────────────────────────
  [Body (SkinnedMeshRenderer)     ] 1,234  [×]
      マスク [◉ BodyMask           ⊙]
  [Face (SkinnedMeshRenderer)     ]   512  [×]
      マスク [None (Texture 2D)    ⊙]
  [ ここへドロップして追加 ]
```

- `element.FindPropertyRelative("strengthMask")` を `EditorGUILayout.PropertyField` で描く。高さは `EditorGUIUtility.singleLineHeight` に指定し、大きなサムネイル表示にしない。`EditorGUI.indentLevel++` で字下げする。
- SerializedProperty 経由で変更するので、Undo と Prefab のオーバーライドは Unity が処理する。既存の `ApplyModifiedProperties()` → `NotifySettingsChanged()` によって、編集中のセッションにもすぐに反映される。
- 対象のメッシュに UV0 が無い場合は、その行に小さな HelpBox（「UV0 が無いためマスクは使われません」）を出す。

### 2.7 オーバーレイ

**変更しない。**

### 2.8 ローカライズ

キーを 3 つ追加する：

- `inspector.mask`：「マスク」/「Mask」
- `inspector.mask_tooltip`
- `inspector.mask_no_uv`

---

## 3. 対象外とするもの

- チャンネルの選択、反転、閾値の設定。

---

## 4. 実装順序と確認

| # | 作業 | 確認 |
|---|---|---|
| 1 | `StrengthMaskUtil` と EditMode テスト | 2×2 の白黒テクスチャで、四隅の値と中央の 0.5 が得られること。読み取り不可・DXT 圧縮のテクスチャでも読めること |
| 2 | `NormalEdit.strengthMask`、`EditSession.Mask.cs`、`BuildInfluences` への掛け算 | 黒く塗った領域を Tilt しても動かないこと。50% のグレーで半分だけ傾くこと。マスクを変えても NDMF のパイプラインが再構築されないこと |
| 3 | 結合グループ内の最小値 | UV シームの片側だけを黒く塗ったとき、反対側も 0% になり、シームに段差が出ないこと。首の継ぎ目で、頭側だけにマスクを指定した場合も同様であること |
| 4 | Inspector の行、ホバー表示、ローカライズ | セッション中に Inspector でマスクを差し替えると、すぐに影響範囲が変わること。Undo で元に戻ること |
| 5 | 再インポートの検知 | マスクを外部で編集して保存すると、セッション中でも反映されること |
