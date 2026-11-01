using System;

namespace Yanshuai.Messages.Core
{
    public static class ProviderEndpoints
    {
        private static string StripEndpoint(string path)
        {
            path = path.TrimEnd('/');
            foreach (var suffix in new[] { "/chat/completions", "/completions", "/messages", "/responses", "/models" })
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return path.Substring(0, path.Length - suffix.Length);
            return path;
        }
        public static string ChatUrl(string url, bool anthropic)
        {
            var builder = new UriBuilder(url.Trim());
            var path = builder.Path.TrimEnd('/');
            if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/completions", StringComparison.OrdinalIgnoreCase)) return builder.Uri.AbsoluteUri;
            path = StripEndpoint(path);
            if (path.Length == 0) path = "/v1";
            builder.Path = path + (anthropic ? "/messages" : "/chat/completions");
            return builder.Uri.AbsoluteUri;
        }
        /// <summary>OpenAI 兼容的语音合成端点：{base}/audio/speech。地址为空时用 OpenAI 官方。</summary>
        public static string SpeechUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "https://api.openai.com/v1/audio/speech";
            var builder = new UriBuilder(url.Trim());
            var path = StripEndpoint(builder.Path);
            if (path.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase)) return builder.Uri.AbsoluteUri;
            if (path.Length == 0) path = "/v1";
            builder.Path = path + "/audio/speech";
            return builder.Uri.AbsoluteUri;
        }

        public static string ModelsUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var builder = new UriBuilder(url.Trim());
            var path = StripEndpoint(builder.Path);
            if (path.Length == 0) path = "/v1";
            builder.Path = path + "/models";
            return builder.Uri.AbsoluteUri;
        }
    }
}
