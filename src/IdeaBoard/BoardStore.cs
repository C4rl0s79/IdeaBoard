using System.Security.Cryptography;
using System.Text.Json;

namespace IdeaBoard;

public sealed class Board
{
    public int Version { get; set; } = 1;
    public long Rev { get; set; }
    public List<Column> Columns { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<Card> Cards { get; set; } = [];
}

public sealed class Column
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Card
{
    public string Id { get; set; } = "";
    public string ColumnId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Priority { get; set; } = "medium";
    /// <summary>User key of the person responsible for the idea and its rollout; "" = nobody yet.</summary>
    public string Assignee { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<Note> Notes { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
}

public sealed class Note
{
    public string Id { get; set; } = "";
    public string By { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Text { get; set; } = "";
}

public sealed class HistoryEntry
{
    public string By { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Text { get; set; } = "";
}

public sealed record CardInput(string? Title, string? Description, List<string>? Tags, string? Priority, string? ColumnId,
    string? ProjectId, string? Assignee);
public sealed record MoveInput(string? ColumnId, int Index);
public sealed record NoteInput(string? Text);
public sealed record NameInput(string? Name);
public sealed record ColumnMoveInput(int Index);

public sealed class BoardException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Board kept in memory, persisted to data/board.json after every change.</summary>
public sealed class BoardStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly string[] DefaultColumns = ["Pomysły", "Do rozważenia", "Zaplanowane", "W realizacji", "Wdrożone", "Odrzucone"];
    static readonly string[] Priorities = ["low", "medium", "high"];
    const int BackupsKept = 30;

    readonly string _path;
    readonly string _backupDir;
    readonly Func<string, string> _displayName;
    readonly Lock _gate = new();
    Board _board;

    /// <param name="displayName">Resolves a user key to a name, for history entries.</param>
    public BoardStore(string dataDir, Func<string, string> displayName)
    {
        _path = Path.Combine(dataDir, "board.json");
        _backupDir = Path.Combine(dataDir, "backups");
        _displayName = displayName;
        if (File.Exists(_path))
        {
            _board = JsonSerializer.Deserialize<Board>(File.ReadAllText(_path), Json)
                ?? throw new InvalidDataException($"{_path} is empty or invalid");
        }
        else
        {
            _board = new Board { Columns = DefaultColumns.Select(n => new Column { Id = NewId(), Name = n }).ToList() };
            Write(JsonSerializer.Serialize(_board, Json));
        }
    }

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    public string Snapshot()
    {
        lock (_gate) return JsonSerializer.Serialize(_board, Json);
    }

    // Changes are applied to a copy, so a validation error halfway through leaves the board untouched.
    string Mutate(Action<Board> change)
    {
        lock (_gate)
        {
            var draft = JsonSerializer.Deserialize<Board>(JsonSerializer.Serialize(_board, Json), Json)!;
            change(draft);
            draft.Rev++;
            var json = JsonSerializer.Serialize(draft, Json);
            Write(json);
            _board = draft;
            return json;
        }
    }

    void Write(string json)
    {
        BackupDaily();
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    void BackupDaily()
    {
        if (!File.Exists(_path)) return;
        Directory.CreateDirectory(_backupDir);
        var target = Path.Combine(_backupDir, $"board-{DateTime.Now:yyyy-MM-dd}.json");
        if (File.Exists(target)) return;
        File.Copy(_path, target);
        foreach (var old in Directory.GetFiles(_backupDir, "board-*.json").OrderDescending().Skip(BackupsKept))
            File.Delete(old);
    }

    static void Log(Card card, string by, string text) =>
        card.History.Add(new HistoryEntry { By = by, At = DateTimeOffset.Now, Text = text });

    // --- cards ---

    public string AddCard(CardInput input, string by) => Mutate(b =>
    {
        var column = input.ColumnId is { Length: > 0 } id ? FindColumn(b, id) : b.Columns[0];
        var now = DateTimeOffset.Now;
        var card = new Card
        {
            Id = NewId(),
            ColumnId = column.Id,
            ProjectId = CleanProjectId(b, input.ProjectId),
            Title = CleanTitle(input.Title),
            Description = CleanText(input.Description, 20000, "Opis"),
            Tags = CleanTags(input.Tags),
            Priority = CleanPriority(input.Priority),
            Assignee = (input.Assignee ?? "").Trim(),
            CreatedBy = by,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Log(card, by, $"Utworzono w: {column.Name}");
        if (card.Assignee.Length > 0) Log(card, by, $"Odpowiedzialny: {_displayName(card.Assignee)}");
        InsertAt(b, card, column.Id, int.MaxValue);
    });

    public string UpdateCard(string id, CardInput input, string by) => Mutate(b =>
    {
        var card = FindCard(b, id);
        if (input.Title is not null) card.Title = CleanTitle(input.Title);
        if (input.Description is not null) card.Description = CleanText(input.Description, 20000, "Opis");
        if (input.Tags is not null) card.Tags = CleanTags(input.Tags);
        if (input.Priority is not null) card.Priority = CleanPriority(input.Priority);
        if (input.ProjectId is not null)
        {
            var projectId = CleanProjectId(b, input.ProjectId);
            if (projectId != card.ProjectId)
            {
                Log(card, by, $"Projekt: {ProjectName(b, card.ProjectId)} → {ProjectName(b, projectId)}");
                card.ProjectId = projectId;
            }
        }
        if (input.Assignee is not null && input.Assignee.Trim() != card.Assignee)
        {
            card.Assignee = input.Assignee.Trim();
            Log(card, by, card.Assignee.Length > 0 ? $"Odpowiedzialny: {_displayName(card.Assignee)}" : "Odpowiedzialny: nikt");
        }
        card.UpdatedAt = DateTimeOffset.Now;
        if (input.ColumnId is { Length: > 0 } columnId && columnId != card.ColumnId)
            MoveCore(b, card, columnId, int.MaxValue, by);
    });

    public string MoveCard(string id, MoveInput input, string by) => Mutate(b =>
        MoveCore(b, FindCard(b, id), input.ColumnId ?? throw new BoardException("Brak kolumny docelowej."), input.Index, by));

    public string DeleteCard(string id) => Mutate(b => b.Cards.Remove(FindCard(b, id)));

    public string AddNote(string id, NoteInput input, string by) => Mutate(b =>
    {
        var card = FindCard(b, id);
        var text = CleanText(input.Text, 5000, "Notatka");
        if (text.Length == 0) throw new BoardException("Notatka jest pusta.");
        card.Notes.Add(new Note { Id = NewId(), By = by, At = DateTimeOffset.Now, Text = text });
        card.UpdatedAt = DateTimeOffset.Now;
    });

    /// <param name="by">Who deletes; only the author or an admin may.</param>
    public string DeleteNote(string id, string noteId, string by, bool isAdmin) => Mutate(b =>
    {
        var card = FindCard(b, id);
        var note = card.Notes.FirstOrDefault(n => n.Id == noteId) ?? throw new BoardException("Nie ma takiej notatki.", 404);
        if (!isAdmin && note.By != by) throw new BoardException("Możesz usuwać tylko własne notatki.", 403);
        card.Notes.Remove(note);
    });

    static void MoveCore(Board b, Card card, string columnId, int index, string by)
    {
        var target = FindColumn(b, columnId);
        b.Cards.Remove(card);
        InsertAt(b, card, target.Id, index);
        if (card.ColumnId != target.Id)
        {
            var from = b.Columns.FirstOrDefault(c => c.Id == card.ColumnId)?.Name ?? "?";
            Log(card, by, $"Przeniesiono: {from} → {target.Name}");
            card.ColumnId = target.Id;
            card.UpdatedAt = DateTimeOffset.Now;
        }
    }

    // Card order within a column is the order of the cards in Board.Cards.
    static void InsertAt(Board b, Card card, string columnId, int index)
    {
        var inColumn = b.Cards.Where(c => c.ColumnId == columnId).ToList();
        index = Math.Clamp(index, 0, inColumn.Count);
        var at = index < inColumn.Count ? b.Cards.IndexOf(inColumn[index])
            : inColumn.Count > 0 ? b.Cards.IndexOf(inColumn[^1]) + 1
            : b.Cards.Count;
        b.Cards.Insert(at, card);
    }

    // --- columns ---

    public string AddColumn(NameInput input) => Mutate(b =>
    {
        if (b.Columns.Count >= 20) throw new BoardException("Za dużo kolumn (maks. 20).");
        b.Columns.Add(new Column { Id = NewId(), Name = CleanName(input.Name, "kolumny") });
    });

    public string RenameColumn(string id, NameInput input) => Mutate(b =>
        FindColumn(b, id).Name = CleanName(input.Name, "kolumny"));

    public string MoveColumn(string id, ColumnMoveInput input) => Mutate(b =>
    {
        var column = FindColumn(b, id);
        b.Columns.Remove(column);
        b.Columns.Insert(Math.Clamp(input.Index, 0, b.Columns.Count), column);
    });

    public string DeleteColumn(string id) => Mutate(b =>
    {
        var column = FindColumn(b, id);
        if (b.Cards.Any(c => c.ColumnId == id)) throw new BoardException("Kolumna nie jest pusta — najpierw przenieś pomysły.", 409);
        if (b.Columns.Count == 1) throw new BoardException("Tablica musi mieć co najmniej jedną kolumnę.", 409);
        b.Columns.Remove(column);
    });

    // --- projects ---

    public string AddProject(NameInput input) => Mutate(b =>
    {
        if (b.Projects.Count >= 100) throw new BoardException("Za dużo projektów (maks. 100).");
        var name = CleanName(input.Name, "projektu");
        if (b.Projects.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new BoardException("Projekt o tej nazwie już istnieje.", 409);
        b.Projects.Add(new Project { Id = NewId(), Name = name });
    });

    public string RenameProject(string id, NameInput input) => Mutate(b =>
        FindProject(b, id).Name = CleanName(input.Name, "projektu"));

    /// <summary>Cards of a deleted project stay on the board, without a project.</summary>
    public string DeleteProject(string id, string by) => Mutate(b =>
    {
        var project = FindProject(b, id);
        foreach (var card in b.Cards.Where(c => c.ProjectId == id))
        {
            card.ProjectId = "";
            Log(card, by, $"Projekt usunięty: {project.Name}");
        }
        b.Projects.Remove(project);
    });

    // --- validation ---

    static Card FindCard(Board b, string id) =>
        b.Cards.FirstOrDefault(c => c.Id == id) ?? throw new BoardException("Nie ma takiego pomysłu (odśwież stronę).", 404);

    static Column FindColumn(Board b, string id) =>
        b.Columns.FirstOrDefault(c => c.Id == id) ?? throw new BoardException("Nie ma takiej kolumny (odśwież stronę).", 404);

    static Project FindProject(Board b, string id) =>
        b.Projects.FirstOrDefault(p => p.Id == id) ?? throw new BoardException("Nie ma takiego projektu (odśwież stronę).", 404);

    static string ProjectName(Board b, string id) =>
        id.Length == 0 ? "brak" : b.Projects.FirstOrDefault(p => p.Id == id)?.Name ?? "?";

    static string CleanProjectId(Board b, string? id) =>
        string.IsNullOrEmpty(id) ? "" : FindProject(b, id).Id;

    static string CleanTitle(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) throw new BoardException("Tytuł jest wymagany.");
        if (t.Length > 200) throw new BoardException("Tytuł może mieć maks. 200 znaków.");
        return t;
    }

    static string CleanName(string? name, string what)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 60) throw new BoardException($"Nazwa {what}: 1–60 znaków.");
        return n;
    }

    static string CleanText(string? text, int max, string what)
    {
        var t = (text ?? "").Trim();
        if (t.Length > max) throw new BoardException($"{what} może mieć maks. {max} znaków.");
        return t;
    }

    static List<string> CleanTags(List<string>? tags)
    {
        var result = (tags ?? [])
            .Select(t => t.Trim().TrimStart('#').Trim())
            .Where(t => t.Length > 0)
            .DistinctBy(t => t.ToLowerInvariant())
            .ToList();
        if (result.Count > 20 || result.Any(t => t.Length > 40)) throw new BoardException("Maks. 20 tagów po 40 znaków.");
        return result;
    }

    static string CleanPriority(string? priority) =>
        priority is null || priority.Length == 0 ? "medium"
        : Priorities.Contains(priority) ? priority
        : throw new BoardException("Nieznany priorytet.");
}
