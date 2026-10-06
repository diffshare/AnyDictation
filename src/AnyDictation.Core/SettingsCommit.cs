namespace AnyDictation;

public enum SaveStatus
{
    Saved,
    /// <summary>保存は成功したが、不要になった資格情報(古いキー)を削除できなかった。</summary>
    SavedWithLeftovers,
    /// <summary>保存に失敗した。設定も、各プロファイルが参照するキーも、保存前のまま。</summary>
    Failed,
}

public readonly record struct SaveResult(SaveStatus Status, string Message);

public static class SettingsCommit
{
    /// <summary>
    /// 設定 JSON とAPIキーの保存。キーは必ず「新しい資格情報 ID」へ書き、設定 JSON の原子的な保存が成功したときに
    /// プロファイルの参照(CredentialId)がその ID へ切り替わる。古いキーの削除は保存成功の後に行う。
    /// そのため途中で失敗しても、アプリの終了や再起動を挟んでも、設定が参照するキーは常に保存前と同じ組のままで、
    /// 旧エンドポイントに新しいキーが組み合わさることはない(元へ戻す処理は不要)。
    /// candidate は呼び出し側が複製したものを渡す(CredentialId はここで書き換える)。
    /// newKeys / keyDeletes のキーはプロファイル ID。
    /// </summary>
    public static SaveResult Commit(
        JsonFileStore<AppSettings> store,
        ICredentialStore creds,
        AppSettings candidate,
        IReadOnlyDictionary<Guid, string> newKeys,
        IReadOnlyCollection<Guid> keyDeletes)
    {
        var stored = store.Value.Profiles.ToDictionary(p => p.Id, p => p.CredentialId);
        var created = new List<Guid>();   // 今回新しく書いた資格情報(失敗時に消す)
        var obsolete = new List<Guid>();  // 保存成功後に不要になる古い資格情報

        try
        {
            foreach (var p in candidate.Profiles)
            {
                stored.TryGetValue(p.Id, out var oldId);
                if (newKeys.TryGetValue(p.Id, out var key))
                {
                    var newId = Guid.NewGuid();
                    creds.Write(CredentialTargets.TargetFor(newId), key);
                    created.Add(newId);
                    p.CredentialId = newId;
                    if (oldId != null) obsolete.Add(oldId.Value);
                }
                else if (keyDeletes.Contains(p.Id))
                {
                    p.CredentialId = null;
                    if (oldId != null) obsolete.Add(oldId.Value);
                }
                else
                {
                    p.CredentialId = oldId; // 画面側の値ではなく、保存済みの参照を引き継ぐ
                }
            }
            var kept = candidate.Profiles.Select(p => p.Id).ToHashSet();
            foreach (var (id, oldId) in stored)
                if (!kept.Contains(id) && oldId != null) obsolete.Add(oldId.Value);

            store.Save(candidate);
        }
        catch (Exception e)
        {
            int leftover = Delete(creds, created);
            return new(SaveStatus.Failed,
                $"保存できませんでした。設定とAPIキーは保存前のままです: {e.Message}" +
                (leftover > 0 ? $"(今回書いた未使用の資格情報 {leftover} 件を削除できませんでした。設定からは参照されません)" : ""));
        }

        int left = Delete(creds, obsolete);
        return left == 0
            ? new(SaveStatus.Saved, "保存しました。")
            : new(SaveStatus.SavedWithLeftovers,
                $"保存しました。ただし、不要になった古いAPIキー {left} 件を資格情報マネージャーから削除できませんでした(設定からは参照されません)。Windows の資格情報マネージャーで「AnyDictation/credential/…」を確認してください。");
    }

    static int Delete(ICredentialStore creds, List<Guid> ids)
    {
        int failed = 0;
        foreach (var id in ids)
        {
            try
            {
                creds.Delete(CredentialTargets.TargetFor(id));
            }
            catch (Exception)
            {
                failed++;
            }
        }
        return failed;
    }
}

public sealed record SendTarget(Profile Profile, string ApiKey);

/// <summary>録音開始・送信の直前に、送ってよい状態かを確認して接続先とキーを解決する。</summary>
public static class SendPreflight
{
    public static bool TryResolve(JsonFileStore<AppSettings> settings, ICredentialStore creds,
        out SendTarget? target, out string problem)
    {
        target = null;
        problem = "";
        if (settings.IsCorrupt)
        {
            problem = "設定ファイルが壊れています。設定画面で内容を確認してください。";
            return false;
        }
        var profile = settings.Value.ActiveProfile;
        if (profile == null)
        {
            problem = "使用するプロファイルが選択されていません。設定画面でプロファイルを追加し、APIキーを入力してください。";
            return false;
        }
        string key;
        try
        {
            key = profile.CredentialId is { } id ? creds.Read(CredentialTargets.TargetFor(id)) ?? "" : "";
        }
        catch (InvalidOperationException e)
        {
            problem = e.Message;
            return false;
        }
        bool loopback = ProfileValidator.IsAllowedEndpoint(profile.Endpoint, out var uri) && uri!.IsLoopback;
        if (key.Length == 0 && !(profile.Provider == ProviderKind.OpenAiCompatible && loopback))
        {
            problem = $"プロファイル「{profile.Name}」のAPIキーが未設定です。設定画面で入力してください。";
            return false;
        }
        target = new SendTarget(profile, key);
        return true;
    }
}
