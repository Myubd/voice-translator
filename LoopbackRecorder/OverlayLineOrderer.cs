using System;
using System.Collections.Generic;

namespace LoopbackRecorder;

/// <summary>
/// オーバーレイに表示する行の並び順を管理する、WPFに依存しない純粋なロジック。
/// Id(TranscriptItem.Id、発話順に単調増加する識別子)昇順を維持したまま、
/// 行の挿入・更新・上限行数超過時の先頭(最古)からの間引きを行う。
///
/// 翻訳ワーカーを複数並列で動かすようになったことで、翻訳の完了順が発話順と
/// 一致しなくなる場合が生じた(例: 先に話した内容がDeepL失敗でOllamaへフォールバックして
/// 遅れている間に、後から話した内容が先に翻訳完了する)。このクラスは、届いた順ではなく
/// Id順に正しい表示位置を計算する責務だけを持つ。実際のUI要素(ListBox)の更新は
/// 呼び出し元(OverlayWindow)がUpsertResultを見て行う(このクラス自身はUIに触れない)。
///
/// OverlayWindow(WPFのWindow)から分離した理由: OverlayWindowはWPF依存のため、
/// このプロジェクトのテストプロジェクト(LoopbackRecorder.Tests、WPF非依存のnet8.0)では
/// 直接ユニットテストできない。並び替えロジックだけをここに切り出すことで、
/// UIを起動せずにテストできるようにしている。
/// </summary>
public sealed class OverlayLineOrderer
{
    private readonly List<(long Id, string Text)> _lines = new();

    public OverlayLineOrderer(int maxLines = 4)
    {
        MaxLines = Math.Max(1, maxLines);
    }

    /// <summary>表示できる最大行数。設定変更時に呼び出し元がセットし直すことを想定しており、
    /// セット時点では自動的なトリムは行わない(明示的にTrimToMax()を呼ぶ必要がある)。</summary>
    public int MaxLines { get; set; }

    /// <summary>現在保持している行(Id昇順)。テストからの検証・デバッグ用に公開している。</summary>
    public IReadOnlyList<(long Id, string Text)> Lines => _lines;

    /// <summary>
    /// Upsert()の呼び出し結果。呼び出し元はこれを見てUI(ListBox)を同期させる。
    /// </summary>
    /// <param name="Index">挿入/更新後の最終的なインデックス。WasTrimmedAwayがtrueの場合は無効(-1)。</param>
    /// <param name="IsUpdate">既存の同じId行を上書きした場合はtrue。新規挿入の場合はfalse。</param>
    /// <param name="IsLatest">この行が現時点で最も新しい発話(=Idが最大)であればtrue。</param>
    /// <param name="WasTrimmedAway">挿入した直後に上限行数超過で自分自身が間引かれてしまった場合はtrue
    /// (自分より新しい行が既にMaxLines件存在していた場合に起こりうる)。</param>
    /// <param name="RemovedFromFrontCount">先頭(最古側)から間引かれた件数。呼び出し元はUI側でも
    /// 同じ件数だけ先頭要素を削除する必要がある。</param>
    public readonly record struct UpsertResult(
        int Index,
        bool IsUpdate,
        bool IsLatest,
        bool WasTrimmedAway,
        int RemovedFromFrontCount);

    /// <summary>Idに対応する行を挿入(新規Idの場合)または更新(既存Idの場合)する。</summary>
    public UpsertResult Upsert(long id, string text)
    {
        var existingIndex = _lines.FindIndex(l => l.Id == id);
        if (existingIndex >= 0)
        {
            // 既に同じIdの行が存在する場合は追加せず更新する。通常は1つのIdにつき1回しか
            // 呼ばれないはずだが、将来的な再翻訳・リトライ通知等にも安全に対応できるようにしておく。
            _lines[existingIndex] = (id, text);
            return new UpsertResult(existingIndex, IsUpdate: true, IsLatest: existingIndex == _lines.Count - 1,
                WasTrimmedAway: false, RemovedFromFrontCount: 0);
        }

        // _linesは常にId昇順を維持している。挿入位置は「自分より大きいIdを持つ最初の要素の手前」。
        // 見つからなければ(=自分が現時点で最も新しい発話であれば)末尾に追加する。
        var insertIndex = _lines.FindIndex(l => l.Id > id);
        if (insertIndex < 0)
        {
            insertIndex = _lines.Count;
        }
        _lines.Insert(insertIndex, (id, text));

        var removedCount = TrimToMax();

        // トリム後に自分自身がまだ残っているかをId基準で再検索する(トリムでインデックスが
        // ずれるため、挿入時のindexをそのまま使い回さずここで数え直すのが安全)。
        var finalIndex = _lines.FindIndex(l => l.Id == id);
        var wasTrimmedAway = finalIndex < 0;
        var isLatest = !wasTrimmedAway && finalIndex == _lines.Count - 1;

        return new UpsertResult(finalIndex, IsUpdate: false, IsLatest: isLatest,
            WasTrimmedAway: wasTrimmedAway, RemovedFromFrontCount: removedCount);
    }

    /// <summary>現在の行数がMaxLinesを超えていれば、先頭(最古)から超過分を間引く。
    /// MaxLinesを変更した直後(表示行数を減らした場合)にも呼び出し元から明示的に呼ぶことを想定。</summary>
    /// <returns>間引いた件数。</returns>
    public int TrimToMax()
    {
        var removed = 0;
        while (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
            removed++;
        }
        return removed;
    }

    /// <summary>保持している行をすべて削除する。</summary>
    public void Clear() => _lines.Clear();
}
