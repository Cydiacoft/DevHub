using System.Security.Principal;
using System.Text.Json;
using DevHub.Core;

if (args.Length != 2 || args[0] != "--environment") return 2;
var requestPath = Path.GetFullPath(args[1]);
try
{
    var request = JsonSerializer.Deserialize<EnvironmentRequest>(await File.ReadAllTextAsync(requestPath), JsonDefaults.Options)
        ?? throw new InvalidDataException("请求为空。");
    var expectedParent = Path.Combine(Path.GetFullPath(request.Root), ".devhub", "data", "environment-requests");
    HubPaths.RequireSafeChild(requestPath, expectedParent);
    if (!File.Exists(Path.Combine(request.Root, "DevHub.root"))) throw new InvalidOperationException("项目标识缺失。");
    if (request.UserSid != WindowsIdentity.GetCurrent().User?.Value) throw new InvalidOperationException("请使用当前账户授权，不能以其他账户修改用户配置。");
    var paths = new HubPaths(request.Root);
    EnvironmentService.ApplyTransaction(request, new WindowsEnvironmentStore(), paths);
    await File.WriteAllTextAsync(requestPath + ".result", "OK");
    return 0;
}
catch (Exception e)
{
    await File.WriteAllTextAsync(requestPath + ".result", e.Message);
    return 1;
}
