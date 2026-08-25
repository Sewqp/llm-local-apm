namespace CollectionServer.Dtos;

public record ManagedServerStatusDto(string Name, bool IsRunning, int? ProcessId, bool IsBuiltIn);

public record CreateManagedServerRequest(string Name, string WorkingDirectory, string Command, string Arguments);
