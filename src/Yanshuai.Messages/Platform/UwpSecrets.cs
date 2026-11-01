// Platform/UwpSecrets.cs —— 凭据保险箱
//
// API Key 只存在 PasswordVault 里，绝不写进 JSON 配置文件。
// PasswordVault 的 resource 固定，用户名用我们的键名（形如 "api.<id>"）。
//
// 注意：PasswordVault 在部分环境下会抛异常（无凭据库、被策略禁用），
// 所有操作都包 try/catch，失败时退化为"本次会话内存有效"。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class UwpSecretStore : ISecretStore
    {
        private const string ResourceName = "Yanshuai.Messages";

        private readonly Dictionary<string, string> _fallback =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private PasswordVault _vault;

        private PasswordVault Vault
        {
            get
            {
                if (_vault == null) _vault = new PasswordVault();
                return _vault;
            }
        }

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            try
            {
                var credential = Vault.Retrieve(ResourceName, key);
                if (credential != null)
                {
                    credential.RetrievePassword();
                    return credential.Password ?? "";
                }
            }
            catch (Exception)
            {
                // 没找到会抛异常，属于正常情况
            }

            string fallback;
            return _fallback.TryGetValue(key, out fallback) ? fallback : "";
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;

            try
            {
                Remove(key);
                if (!string.IsNullOrEmpty(value))
                {
                    Vault.Add(new PasswordCredential(ResourceName, key, value));
                }
                return;
            }
            catch (Exception)
            {
                // 退化到内存
            }

            if (string.IsNullOrEmpty(value)) _fallback.Remove(key);
            else _fallback[key] = value;
        }

        public void Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            try
            {
                var credential = Vault.Retrieve(ResourceName, key);
                if (credential != null) Vault.Remove(credential);
            }
            catch (Exception) { }

            _fallback.Remove(key);
        }

        /// <summary>清空本应用写过的全部凭据（"重置所有密钥"用）。</summary>
        public int ClearAll()
        {
            int removed = 0;
            try
            {
                var all = Vault.RetrieveAll();
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(all[i].Resource, ResourceName, StringComparison.Ordinal))
                    {
                        all[i].RetrievePassword();
                        Vault.Remove(all[i]);
                        removed++;
                    }
                }
            }
            catch (Exception) { }

            removed += _fallback.Count;
            _fallback.Clear();
            return removed;
        }

        public Task<int> ClearAllAsync()
        {
            return Task.FromResult(ClearAll());
        }
    }
}
