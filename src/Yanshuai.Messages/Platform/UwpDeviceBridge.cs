// Platform/UwpDeviceBridge.cs —— 设备能力（UWP 实现）
//
// 有些 WinRT API 必须在 UI 线程调用（日历、联系人、媒体控制），
// 这里统一用 OnUiThreadAsync 包一层，Core 侧完全无感。
//
// 已知限制（诚实写在这里，别让模型以为能做）：
//   • 系统音量的读写在 UWP 上没有公开 API，volume_up / volume_down / mute 一律返回"不支持"。
//   • 拨号与发短信只是打开系统应用并填好内容，最终仍需用户自己按下。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Appointments;
using Windows.ApplicationModel.Contacts;
using Windows.Foundation;
using Windows.Media;
using Windows.System;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class UwpDeviceBridge : IDeviceBridge
    {
        private readonly IFileStore _files;

        public UwpDeviceBridge(IFileStore files)
        {
            _files = files;
        }

        public string DeviceFamily
        {
            get
            {
                try
                {
                    var info = Windows.System.Profile.AnalyticsInfo.VersionInfo;
                    return info.DeviceFamily + " " + info.DeviceFamilyVersion;
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        // ── UI 线程编排 ──────────────────────────────────────────────────

        private static Task<T> OnUiThreadAsync<T>(Func<Task<T>> action)
        {
            var main = Windows.ApplicationModel.Core.CoreApplication.MainView;
            if (main == null || main.CoreWindow == null)
            {
                return action();
            }

            var dispatcher = main.CoreWindow.Dispatcher;
            if (dispatcher.HasThreadAccess)
            {
                return action();
            }

            var completion = new TaskCompletionSource<T>();
            var ignored = dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async delegate
            {
                try
                {
                    completion.TrySetResult(await action().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return completion.Task;
        }

        // ── 日历 ────────────────────────────────────────────────────────

        public async Task<IReadOnlyList<string>> ListCalendarAsync(int max, CancellationToken ct)
        {
            var result = new List<string>();

            await OnUiThreadAsync<object>(async delegate
            {
                var store = await AppointmentManager
                    .RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);

                var now = DateTimeOffset.Now;
                var appointments = await store.FindAppointmentsAsync(now, TimeSpan.FromDays(30));

                int count = 0;
                foreach (var appointment in appointments)
                {
                    if (count >= max) break;
                    result.Add(FormatAppointment(appointment));
                    count++;
                }
                return null;
            }).ConfigureAwait(false);

            return result;
        }

        public async Task<string> CreateCalendarEventAsync(
            string title, DateTimeOffset start, int minutes, string notes, CancellationToken ct)
        {
            // UWP 的 AppointmentStore 没有公开的"直接写入"接口，官方路径是
            // AppointmentManager.ShowAddAppointmentAsync：弹系统确认界面，用户点确认才落下。
            // 这跟我们的审批模型正好一致 —— 我们自己先问一次，系统再确认一次。
            var message = await OnUiThreadAsync<string>(async delegate
            {
                var appointment = new Windows.ApplicationModel.Appointments.Appointment
                {
                    Subject = title ?? "",
                    StartTime = start,
                    Duration = TimeSpan.FromMinutes(minutes <= 0 ? 60 : minutes),
                    Details = notes ?? ""
                };

                var selection = new Rect(0, 0, 1, 1);
                var id = await AppointmentManager.ShowAddAppointmentAsync(appointment, selection);

                if (string.IsNullOrEmpty(id))
                {
                    return "用户在系统界面取消了，未创建日程。";
                }

                return "已创建日程：" + appointment.Subject + "（" +
                       start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "）";
            }).ConfigureAwait(false);

            return message;
        }

        private static string FormatAppointment(Windows.ApplicationModel.Appointments.Appointment appointment)
        {
            var sb = new StringBuilder();
            sb.Append(appointment.StartTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
            if (appointment.Duration > TimeSpan.Zero)
            {
                sb.Append("（").Append((int)appointment.Duration.TotalMinutes).Append(" 分钟）");
            }
            sb.Append(" ").Append(string.IsNullOrEmpty(appointment.Subject) ? "(无标题)" : appointment.Subject);
            if (!string.IsNullOrEmpty(appointment.Location)) sb.Append(" · ").Append(appointment.Location);
            return sb.ToString();
        }

        // ── 联系人 ──────────────────────────────────────────────────────

        public async Task<IReadOnlyList<string>> SearchContactsAsync(string query, int max, CancellationToken ct)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(query)) return result;

            await OnUiThreadAsync<object>(async delegate
            {
                var store = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AllContactsReadOnly);
                var contacts = await store.FindContactsAsync(query);

                int count = 0;
                foreach (var contact in contacts)
                {
                    if (count >= max) break;

                    var sb = new StringBuilder();
                    sb.Append(contact.DisplayName ?? "(无名)");

                    if (contact.Phones != null && contact.Phones.Count > 0)
                    {
                        sb.Append(" · ").Append(contact.Phones[0].Number);
                    }
                    result.Add(sb.ToString());
                    count++;
                }
                return null;
            }).ConfigureAwait(false);

            return result;
        }

        // ── 启动 ────────────────────────────────────────────────────────

        public async Task<string> LaunchUriAsync(string uri, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(uri)) return "没有要打开的目标。";

            Uri parsed;
            if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out parsed))
            {
                return "目标格式不正确。";
            }

            try
            {
                var ok = await Launcher.LaunchUriAsync(parsed);
                return ok ? "" : "系统拒绝打开这个目标。";
            }
            catch (Exception)
            {
                return "无法打开这个目标。";
            }
        }

        // ── 媒体 ────────────────────────────────────────────────────────

        public async Task<string> MediaControlAsync(string action, CancellationToken ct)
        {
            var normalized = (action ?? "").ToLowerInvariant();

            if (normalized == "volume_up" || normalized == "volume_down" || normalized == "mute")
            {
                return "这个系统没有开放调整系统音量的接口，音量请用机身音量键。";
            }

            var message = await OnUiThreadAsync<string>(delegate
            {
                var controls = SystemMediaTransportControls.GetForCurrentView();
                controls.IsEnabled = true;

                switch (normalized)
                {
                    case "play_pause":
                        controls.PlaybackStatus = controls.PlaybackStatus == MediaPlaybackStatus.Playing
                            ? MediaPlaybackStatus.Paused
                            : MediaPlaybackStatus.Playing;
                        return Task.FromResult("已切换播放/暂停。");

                    case "next":
                        // SMTC 只能把按键转交给真正在播放的应用；这里没有订阅方，
                        // 与其假装成功，不如如实说明。
                        return Task.FromResult("下一首需要由正在播放的应用响应，当前没有可转交的播放器。");

                    case "previous":
                        return Task.FromResult("上一首需要由正在播放的应用响应，当前没有可转交的播放器。");

                    default:
                        return Task.FromResult("不认识的动作：" + action);
                }
            }).ConfigureAwait(false);

            return message;
        }

        // ── 读图 ────────────────────────────────────────────────────────

        public async Task<string> ReadImageAsBase64Async(string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path) || _files == null) return "";

            var bytes = await _files.ReadBytesAsync(path).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return "";

            return Convert.ToBase64String(bytes);
        }
    }
}
