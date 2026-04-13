using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Reactive;
using Avalonia.Controls;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using HttpClient.Models;

namespace HttpClient.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient();
        private string _serverUrl = "http://localhost:5096";
        private string _login = "";
        private string _password = "";
        private string? _jwtToken = null;
        private string _status = "Not connected";
        private List<FileItem> _files = new();
        private FileItem _selectedFile = new();

        public string ServerUrl
        {
            get => _serverUrl;
            set
            {
                this.RaiseAndSetIfChanged(ref _serverUrl, value);
                _jwtToken = null;
                _httpClient.DefaultRequestHeaders.Authorization = null;
                Status = "Not authenticated. Please login.";
                Files.Clear();
            }
        }

        public string Login
        {
            get => _login;
            set => this.RaiseAndSetIfChanged(ref _login, value);
        }

        public string Password
        {
            get => _password;
            set => this.RaiseAndSetIfChanged(ref _password, value);
        }

        public string Status
        {
            get => _status;
            set => this.RaiseAndSetIfChanged(ref _status, value);
        }

        public List<FileItem> Files
        {
            get => _files;
            set => this.RaiseAndSetIfChanged(ref _files, value);
        }

        public FileItem SelectedFile
        {
            get => _selectedFile;
            set => this.RaiseAndSetIfChanged(ref _selectedFile, value);
        }

        public ReactiveCommand<Unit, Unit> LoginCommand { get; }
        public ReactiveCommand<Unit, Unit> RegisterCommand { get; }
        public ReactiveCommand<Unit, Unit> UploadFileCommand { get; }
        public ReactiveCommand<Unit, Unit> DownloadAndOpenCommand { get; }
        public ReactiveCommand<Unit, Unit> DeleteFileCommand { get; }
        public ReactiveCommand<Unit, Unit> CopyFileCommand { get; }
        public ReactiveCommand<Unit, Unit> MoveFileCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        public MainWindowViewModel()
        {
            LoginCommand = ReactiveCommand.CreateFromTask(LoginAsync);
            RegisterCommand = ReactiveCommand.CreateFromTask(RegisterAsync);
            UploadFileCommand = ReactiveCommand.CreateFromTask(UploadFileAsync);
            DownloadAndOpenCommand = ReactiveCommand.CreateFromTask(DownloadAndOpenAsync);
            DeleteFileCommand = ReactiveCommand.CreateFromTask(DeleteFileAsync);
            CopyFileCommand = ReactiveCommand.CreateFromTask(CopyFileAsync);
            MoveFileCommand = ReactiveCommand.CreateFromTask(MoveFileAsync);
            RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        }

        private Window? GetMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        private async Task LoginAsync()
        {
            if (string.IsNullOrWhiteSpace(Login) || string.IsNullOrWhiteSpace(Password))
            {
                Status = "Enter login and password";
                return;
            }

            try
            {
                var loginData = new { Login = Login?.Trim(), Password = Password?.Trim() };         
                var response = await _httpClient.PostAsJsonAsync($"{ServerUrl}/auth/login", loginData);
                if (!response.IsSuccessStatusCode)
                {
                    Status = "Login failed: invalid credentials";
                    return;
                }

                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                _jwtToken = result?.Token;
                if (string.IsNullOrEmpty(_jwtToken))
                {
                    Status = "Login failed: no token received";
                    return;
                }

                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);

                Status = $"Logged in as {Login}";
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Status = $"Login error: {ex.Message}";
            }
        }

        private async Task RegisterAsync()
        {
            // Простой диалог для ввода логина и пароля
            var (login, password) = await ShowRegistrationDialog();
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
                return;

            try
            {
                var registerData = new { Login = login, Password = password };                
                var response = await _httpClient.PostAsJsonAsync($"{ServerUrl}/auth/register", registerData);
                if (response.IsSuccessStatusCode)
                {
                    Status = "Registration successful. You can now login.";
                    // Автоматически заполним поля логина/пароля
                    Login = login;
                    Password = password;
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Status = $"Registration failed: {error}";
                }
            }
            catch (Exception ex)
            {
                Status = $"Registration error: {ex.Message}";
            }
        }

        private async Task<(string login, string password)> ShowRegistrationDialog()
        {
            var tcs = new TaskCompletionSource<(string, string)>();

            var loginBox = new TextBox { Width = 250, Watermark = "Login", Margin = new Thickness(5) };
            var passBox = new TextBox { Width = 250, Watermark = "Password", Margin = new Thickness(5), PasswordChar = '*' };            
            var okButton = new Button { Content = "Register", Width = 100, Margin = new Thickness(5) };
            var cancelButton = new Button { Content = "Cancel", Width = 100, Margin = new Thickness(5) };

            var panel = new StackPanel
            {
                Margin = new Thickness(10),
                Children = { loginBox, passBox, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { okButton, cancelButton } } }
            };

            var dialog = new Window
            {
                Title = "Register new user",
                Content = panel,
                Width = 350,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            okButton.Click += (_, _) => { tcs.SetResult((loginBox.Text ?? "", passBox.Text ?? "")); dialog.Close(); };
            cancelButton.Click += (_, _) => { tcs.SetResult(("", "")); dialog.Close(); };

            var mainWindow = GetMainWindow();
            if (mainWindow != null) await dialog.ShowDialog(mainWindow);
            else dialog.Show();

            return await tcs.Task;
        }

        // ========== Работа с файлами (требуют авторизации) ==========
        private void EnsureAuthenticated()
        {
            if (string.IsNullOrEmpty(_jwtToken))
                throw new InvalidOperationException("Not authenticated. Please login first.");
        }

        private async Task<T?> SendAuthenticatedRequest<T>(Func<Task<T>> requestFunc, string errorContext)
        {
            try
            {
                EnsureAuthenticated();
                return await requestFunc();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Status = "Session expired or unauthorized. Please login again.";
                _jwtToken = null;
                _httpClient.DefaultRequestHeaders.Authorization = null;
                return default;
            }
            catch (Exception ex)
            {
                Status = $"{errorContext}: {ex.Message}";
                return default;
            }
        }

        private async Task RefreshAsync()
        {
            await SendAuthenticatedRequest(async () =>
            {
                var fileNames = await _httpClient.GetFromJsonAsync<List<string>>($"{ServerUrl}/");
                Files = fileNames?.Select(n => new FileItem { Name = n }).ToList() ?? new List<FileItem>();
                Status = $"Loaded {Files.Count} files";
                return true;
            }, "Refresh failed");
        }

        private async Task UploadFileAsync()
        {
            var mainWindow = GetMainWindow();
            if (mainWindow == null) return;

            var dialog = new OpenFileDialog { AllowMultiple = false };
            var result = await dialog.ShowAsync(mainWindow);
            if (result == null || result.Length == 0) return;

            var localPath = result[0];
            var fileName = Path.GetFileName(localPath);

            await SendAuthenticatedRequest(async () =>
            {
                await using var fileStream = File.OpenRead(localPath);
                var content = new StreamContent(fileStream);
                var response = await _httpClient.PutAsync($"{ServerUrl}/{Uri.EscapeDataString(fileName)}", content);
                response.EnsureSuccessStatusCode();
                Status = $"Uploaded {fileName}";
                await RefreshAsync();
                return true;
            }, "Upload failed");
        }

        private async Task DownloadAndOpenAsync()
        {
            if (string.IsNullOrEmpty(SelectedFile?.Name))
            {
                Status = "Select a file first";
                return;
            }

            await SendAuthenticatedRequest(async () =>
            {
                var appDirectory = AppContext.BaseDirectory;
                var saveDirectory = Path.Combine(appDirectory, "OpenedFiles");
                Directory.CreateDirectory(saveDirectory);

                var localPath = Path.Combine(saveDirectory, SelectedFile.Name);

                using var response = await _httpClient.GetAsync($"{ServerUrl}/{Uri.EscapeDataString(SelectedFile.Name)}", HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using var fileStream = File.Create(localPath);
                await response.Content.CopyToAsync(fileStream);

                var mainWindow = GetMainWindow();
                if (mainWindow == null) return true;

                var launcher = TopLevel.GetTopLevel(mainWindow)?.Launcher;
                if (launcher == null) return true;

                await launcher.LaunchFileInfoAsync(new FileInfo(localPath));
                Status = $"Opened {SelectedFile.Name} (cached in {saveDirectory})";
                return true;
            }, "Download/open failed");
        }

        private async Task DeleteFileAsync()
        {
            if (string.IsNullOrEmpty(SelectedFile?.Name)) return;

            await SendAuthenticatedRequest(async () =>
            {
                var response = await _httpClient.DeleteAsync($"{ServerUrl}/{Uri.EscapeDataString(SelectedFile.Name)}");
                response.EnsureSuccessStatusCode();
                Status = $"Deleted {SelectedFile.Name}";
                await RefreshAsync();
                return true;
            }, "Delete failed");
        }

        private async Task CopyFileAsync()
        {
            if (string.IsNullOrEmpty(SelectedFile?.Name)) return;

            var destName = await ShowInputDialog("Copy file", $"Destination name for '{SelectedFile.Name}':");
            if (string.IsNullOrWhiteSpace(destName)) return;

            await SendAuthenticatedRequest(async () =>
            {
                var request = new { Source = SelectedFile.Name, Destination = destName };
                var response = await _httpClient.PostAsJsonAsync($"{ServerUrl}/copy", request);
                response.EnsureSuccessStatusCode();
                Status = $"Copied to {destName}";
                await RefreshAsync();
                return true;
            }, "Copy failed");
        }

        private async Task MoveFileAsync()
        {
            if (string.IsNullOrEmpty(SelectedFile?.Name)) return;

            var destName = await ShowInputDialog("Move file", $"New name for '{SelectedFile.Name}':");
            if (string.IsNullOrWhiteSpace(destName)) return;

            await SendAuthenticatedRequest(async () =>
            {
                var request = new { Source = SelectedFile.Name, Destination = destName };
                var response = await _httpClient.PostAsJsonAsync($"{ServerUrl}/move", request);
                response.EnsureSuccessStatusCode();
                Status = $"Moved to {destName}";
                await RefreshAsync();
                return true;
            }, "Move failed");
        }

        // Вспомогательный диалог
        private async Task<string> ShowInputDialog(string title, string prompt)
        {
            var tcs = new TaskCompletionSource<string>();
            var inputBox = new TextBox { Width = 300, Watermark = prompt, Margin = new Thickness(5) };
            var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(5) };
            var cancelButton = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(5) };

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { okButton, cancelButton } };
            var mainPanel = new StackPanel { Margin = new Thickness(10), Children = { inputBox, buttonPanel } };

            var dialog = new Window
            {
                Title = title,
                Content = mainPanel,
                Width = 420,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            okButton.Click += (_, _) => { tcs.SetResult(inputBox.Text ?? ""); dialog.Close(); };
            cancelButton.Click += (_, _) => { tcs.SetResult(""); dialog.Close(); };

            var mainWindow = GetMainWindow();
            if (mainWindow != null) await dialog.ShowDialog(mainWindow);
            else dialog.Show();

            return await tcs.Task;
        }
    }

    // Вспомогательные классы для JSON
    public class LoginResponse
    {
        public string Token { get; set; } = "";
    }
}