using System.Diagnostics;

long MemoryLimit = 64 * 1024 * 1024;

Console.Write(" Ограничение по времени [ЧЧ:ММ:СС.млс]: ");
var timeLimit = TimeSpan.Parse(Console.ReadLine()!);
Console.Write("Путь к программе MASTER: ");
var masterPath = Console.ReadLine()!;
Console.Write(" Путь к программе SLAVE: ");
var slavePath = Console.ReadLine()!;
Console.Write("  Сохранить дамп? (y/*): ");
var doDump = Console.ReadKey().Key == ConsoleKey.Y;
Console.WriteLine();

var masterExec = Process.Start(ProcessConfig(masterPath))!;
var slaveExec = Process.Start(ProcessConfig(slavePath))!;
var output = File.CreateText("game.log");

var masterStdErr = Task.Run(() => RedirectStandardError(Role.Master));
var slaveStdErr = Task.Run(() => RedirectStandardError(Role.Slave));

Console.WriteLine("-- Создание игр...");

ExpectOk(Role.Master, "create master", "master не смог создать игру");
ExpectOk(Role.Slave, "create slave", "slave не смог создать игру");

Console.WriteLine("-- Передача параметров...");
MoveParameter("width");
MoveParameter("height");
for (var i = 1; i <= 4; ++i)
    MoveParameter($"count {i}");

Console.WriteLine("-- Запуск игры...");
ExpectOk(Role.Master, "start", "master не смог запустить игру");
ExpectOk(Role.Slave, "start", "slave не смог запустить игру");

if (doDump)
{
    Console.WriteLine("-- Сохранение карт...");
    ExpectOk(Role.Master, $"dump {LocalPath("master.dump")}", "master не смог сохранить карту");
    ExpectOk(Role.Slave, $"dump {LocalPath("slave.dump")}", "slave не смог сохранить карту");
}

Console.WriteLine("-- Партия идёт...");
var nextMove = Role.Master;
UInt128 moves = 0;
Console.WriteLine();

while (true)
{
    Console.CursorTop--;
    Console.WriteLine($"Ход {++moves}");

    var acceptMove = nextMove;
    nextMove = nextMove switch
    {
        Role.Master => Role.Slave,
        Role.Slave => Role.Master,
        _ => throw null!
    };

    WriteTo(nextMove, "shot");
    if (!ReadFrom(nextMove, out var rawPosition)) Finish(-1);
    var positionTokens = rawPosition.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if ((positionTokens.Length != 2) || 
        !ulong.TryParse(positionTokens[0], out var x) || 
        !ulong.TryParse(positionTokens[1], out var y))
    {
        Console.WriteLine($"{RoleTag(nextMove)} выдал некорректные координаты: {rawPosition}");
        Finish(-1);
        return;
    }

    WriteTo(acceptMove, $"shot {x} {y}");
    if (!ReadFrom(acceptMove, out var response)) Finish(-1);
    response = response.Trim();
    if ((response != "miss") && (response != "hit") && (response != "kill"))
    {
        Console.WriteLine($"{RoleTag(acceptMove)} выдал некорректный ответ на выстрел: {response}");
        Finish(-1);
        return;
    }

    ExpectOk(nextMove, $"set result {response}", $"{RoleTag(nextMove)} не принял результат своего выстрела");

    var masterFinish = ExpectYesNo(Role.Master, "finished", "master не выдал ответ на finished");
    var masterWin = ExpectYesNo(Role.Master, "win", "master не выдал ответ на win");
    var masterLose = ExpectYesNo(Role.Master, "lose", "master не выдал ответ на lose");

    var slaveFinish = ExpectYesNo(Role.Slave, "finished", "slave не выдал ответ на finished");
    var slaveWin = ExpectYesNo(Role.Slave, "win", "slave не выдал ответ на win");
    var slaveLose = ExpectYesNo(Role.Slave, "lose", "slave не выдал ответ на lose");

    if (masterFinish != slaveFinish)
    {
        Console.WriteLine("master и slave не согласны в том, закончилась ли партия");
        Finish(-1);
    }
    if (!masterFinish)
    {
        if (slaveWin || slaveLose)
        {
            Console.WriteLine("slave считает себя победившим или проигравшим, хотя он согласен с тем, что партия не кончилась");
            Finish(-1);
        }
        if (masterWin || masterLose)
        {
            Console.WriteLine("master считает себя победившим или проигравшим, хотя он согласен с тем, что партия не кончилась");
            Finish(-1);
        }
        continue;
    }
    if (masterWin)  Console.WriteLine("MASTER считает, что MASTER победил");
    if (masterLose) Console.WriteLine("MASTER считает, что SLAVE  победил");
    if (slaveWin)   Console.WriteLine("SLAVE  считает, что MASTER победил");
    if (slaveLose)  Console.WriteLine("SLAVE  считает, что SLAVE  победил");
    Finish(0);
}

string LocalPath(string filename) => 
    Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, filename);

void MoveParameter(string name)
{
    WriteTo(Role.Master, $"get {name}");
    if (!ReadFrom(Role.Master, out var response)) Finish(-1);
    if (!ulong.TryParse(response.Trim(), out var value))
    {
        Console.WriteLine($"master вернул некорркетное значение для '{name}' = {response.Trim()}");
        Finish(-1);
    }

    ExpectOk(Role.Slave, $"set {name} {value}", $"slave не смог принять параметр '{name}' = {value}");
}

bool ExpectYesNo(Role from, string request, string error)
{
    WriteTo(from, request);
    if (!ReadFrom(from, out var response)) Finish(-1);
    response = response.Trim();
    if (response == "yes") return true;
    if (response == "no") return false;
    Console.WriteLine(error);
    Finish(-1);
    return false;
}

void ExpectOk(Role from, string request, string error)
{
    WriteTo(from, request);
    if (!ReadFrom(from, out var response)) Finish(-1);
    if (response.Trim() != "ok")
    {
        Console.WriteLine(error);
        Finish(-1);
    }
}

void Finish(int exit)
{
    masterExec.Kill();
    slaveExec.Kill();
    masterStdErr.Wait();
    slaveStdErr.Wait();
    output.Dispose();
    Environment.Exit(exit);
}

bool ReadFrom(Role role, out string s)
{
    var from = role switch
    {
        Role.Master => masterExec,
        Role.Slave => slaveExec,
        _ => throw null!
    };
    if (from.WorkingSet64 > MemoryLimit)
    {
        Console.WriteLine($"Превышение памяти у {RoleTag(role)}: {from.WorkingSet64 / 1048576.0:F1}МБ");
        Finish(-1);
        s = null!;
        return false;
    }
    if (from.UserProcessorTime > timeLimit)
    {
        Console.WriteLine($"Превышение времени у {RoleTag(role)}: {from.UserProcessorTime}");
        Finish(-1);
        s = null!;
        return false;
    }

    try
    {
        s = from.StandardOutput.ReadLine()!;
        lock (output)
        {
            output.WriteLine($"[{RoleTag(role)} STDOUT]: {s}");
            output.Flush();
        }
        return true;
    }
    catch (Exception e)
    {
        Console.WriteLine($"Не удалось считать от {RoleTag(role)}: {e}");
        s = null!;
        return false;
    }
}

void WriteTo(Role role, string line)
{
    var to = role switch
    {
        Role.Master => masterExec,
        Role.Slave => slaveExec,
        _ => throw null!
    };
    if (to.WorkingSet64 > MemoryLimit)
    {
        Console.WriteLine($"Превышение памяти у {RoleTag(role)}: {to.WorkingSet64 / 1048576.0:F1}МБ");
        Finish(-1);
        return;
    }
    if (to.UserProcessorTime > timeLimit)
    {
        Console.WriteLine($"Превышение времени у {RoleTag(role)}: {to.UserProcessorTime}");
        Finish(-1);
        return;
    }

    try
    {
        to.StandardInput.WriteLine(line);
        to.StandardInput.Flush();
        lock (output)
        {
            output.WriteLine($"[{RoleTag(role)} STDINP]: {line}");
            output.Flush();
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"Не удалось записать в {RoleTag(role)}: {e}");
    }
}

void RedirectStandardError(Role role)
{
    var from = (role switch
    {
        Role.Master => masterExec,
        Role.Slave => slaveExec,
        _ => throw null!
    }).StandardError;

    try
    {
        while (!from.EndOfStream)
        {
            var line = from.ReadLine();
            lock (output)
            {
                output.WriteLine($"[{RoleTag(role)} STDERR]: {line}");
                output.Flush();
            }
        }
    }
    catch
    {
    }
}

static ProcessStartInfo ProcessConfig(string masterPath) => 
    new(masterPath)
    {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

static string RoleTag(Role role) => role switch
{
    Role.Master => "MASTER",
    Role.Slave => "SLAVE ",
    _ => "??????"
};

enum Role
{
    Master,
    Slave
}