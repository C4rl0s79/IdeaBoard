using System.Security.Cryptography;
using System.Text.Json;

namespace IdeaBoard;

public sealed class Board
{
    public int Version { get; set; } = 1;
    public long Rev { get; set; }
    public List<Column> Columns { get; set; } = [];
    public List<Card> Cards { get; set; } = [];
}

public sealed class Column
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Card
{
    public string Id { get; set; } = "";
    public string ColumnId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Priority { get; set; } = "medium";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<Note> Notes { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
}

public sealed class Note
{
    public string Id { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Text { get; set; } = "";
}

public sealed class HistoryEntry
{
    public DateTimeOffset At { get; set; }
    public string Text { get; set; } = "";
}

public sealed record CardInput(string? Title, string? Description, List<string>? Tags, string? Priority, string? ColumnId);
public sealed record MoveInput(string? ColumnId, int Index);
public sealed record NoteInput(string? Text);
public sealed record ColumnInput(string? Name);
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
    readonly Lock _gate = new();
    Board _board;

    public BoardStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "board.json");
        _backupDir = Path.Combine(dataDir, "backups");
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

    // --- cards ---

    public string AddCard(CardInput input) => Mutate(b =>
    {
        var column = input.ColumnId is { Length: > 0 } id ? FindColumn(b, id) : b.Columns[0];
        var now = DateTimeOffset.Now;
        var card = new Card
        {
            Id = NewId(),
            ColumnId = column.Id,
            Title = CleanTitle(input.Title),
            Description = CleanText(input.Description, 20000, "Opis"),
            Tags = CleanTags(input.Tags),
            Priority = CleanPriority(input.Priority),
            CreatedAt = now,
            UpdatedAt = now,
        };
        card.History.Add(new HistoryEntry { At = now, Text = $"Utworzono w: {column.Name}" });
        InsertAt(b, card, column.Id, int.MaxValue);
    });

    public string UpdateCard(string id, CardInput input) => Mutate(b =>
    {
        var card = FindCard(b, id);
        if (input.Title is not null) card.Title = CleanTitle(input.Title);
        if (input.Description is not null) card.Description = CleanText(input.Description, 20000, "Opis");
        if (input.Tags is not null) card.Tags = CleanTags(input.Tags);
        if (input.Priority is not null) card.Priority = CleanPriority(input.Priority);
        card.UpdatedAt = DateTimeOffset.Now;
        if (input.ColumnId is { Length: > 0 } columnId && columnId != card.ColumnId)
            MoveCore(b, card, columnId, int.MaxValue);
    });

    public string MoveCard(string id, MoveInput input) => Mutate(b =>
        MoveCore(b, FindCard(b, id), input.ColumnId ?? throw new BoardException("Brak kolumny docelowej."), input.Index));

    public string DeleteCard(string id) => Mutate(b => b.Cards.Remove(FindCard(b, id)));

    public string AddNote(string id, NoteInput input) => Mutate(b =>
    {
        var card = FindCard(b, id);
        var text = CleanText(input.Text, 5000, "Notatka");
        if (text.Length == 0) throw new BoardException("Notatka jest pusta.");
        card.Notes.Add(new Note { Id = NewId(), At = DateTimeOffset.Now, Text = text });
        card.UpdatedAt = DateTimeOffset.Now;
    });

    public string DeleteNote(string id, string noteId) => Mutate(b =>
    {
        var card = FindCard(b, id);
        if (card.Notes.RemoveAll(n => n.Id == noteId) == 0) throw new BoardException("Nie ma takiej notatki.", 404);
    });

    static void MoveCore(Board b, Card card, string columnId, int index)
    {
        var target = FindColumn(b, columnId);
        b.Cards.Remove(card);
        InsertAt(b, card, target.Id, index);
        if (card.ColumnId != target.Id)
        {
            var from = b.Columns.FirstOrDefault(c => c.Id == card.ColumnId)?.Name ?? "?";
            card.History.Add(new HistoryEntry { At = DateTimeOffset.Now, Text = $"Przeniesiono: {from} → {target.Name}" });
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

    public string AddColumn(ColumnInput input) => Mutate(b =>
    {
        if (b.Columns.Count >= 20) throw new BoardException("Za dużo kolumn (maks. 20).");
        b.Columns.Add(new Column { Id = NewId(), Name = CleanColumnName(input.Name) });
    });

    public string RenameColumn(string id, ColumnInput input) => Mutate(b =>
        FindColumn(b, id).Name = CleanColumnName(input.Name));

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

    // --- validation ---

    static Card FindCard(Board b, string id) =>
        b.Cards.FirstOrDefault(c => c.Id == id) ?? throw new BoardException("Nie ma takiego pomysłu (odśwież stronę).", 404);

    static Column FindColumn(Board b, string id) =>
        b.Columns.FirstOrDefault(c => c.Id == id) ?? throw new BoardException("Nie ma takiej kolumny (odśwież stronę).", 404);

    static string CleanTitle(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) throw new BoardException("Tytuł jest wymagany.");
        if (t.Length > 200) throw new BoardException("Tytuł może mieć maks. 200 znaków.");
        return t;
    }

    static string CleanColumnName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 60) throw new BoardException("Nazwa kolumny: 1–60 znaków.");
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
