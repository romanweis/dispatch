using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dispatch.Api.Data;

public sealed class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunEvent> RunEvents => Set<RunEvent>();
    public DbSet<ProgressNote> ProgressNotes => Set<ProgressNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // jsonb only exists on PostgreSQL; tests run on Sqlite where the same value converter yields TEXT.
        var isNpgsql = Database.IsNpgsql();

        var nullableJsonConverter = new ValueConverter<JsonDocument?, string?>(
            d => d == null ? null : d.RootElement.GetRawText(),
            s => s == null ? null : JsonDocument.Parse(s, default));
        var nullableJsonComparer = new ValueComparer<JsonDocument?>(
            (a, b) => JsonText(a) == JsonText(b),
            d => JsonText(d) == null ? 0 : JsonText(d)!.GetHashCode(),
            d => d == null ? null : JsonDocument.Parse(d.RootElement.GetRawText(), default));
        var jsonConverter = new ValueConverter<JsonDocument, string>(
            d => d.RootElement.GetRawText(),
            s => JsonDocument.Parse(s, default));
        var jsonComparer = new ValueComparer<JsonDocument>(
            (a, b) => JsonText(a) == JsonText(b),
            d => JsonText(d)!.GetHashCode(),
            d => JsonDocument.Parse(d.RootElement.GetRawText(), default));

        modelBuilder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.Name).HasMaxLength(100);
            if (isNpgsql)
            {
                e.Property(p => p.ReposJson).HasColumnType("jsonb");
            }
        });

        modelBuilder.Entity<Ticket>(e =>
        {
            e.ToTable("tickets");
            e.Property(t => t.Status).HasConversion(new SnakeCaseEnumConverter<TicketStatus>()).HasMaxLength(32);
            e.Property(t => t.Type).HasConversion(new SnakeCaseEnumConverter<TicketType>()).HasMaxLength(16);
            e.Property(t => t.Token).HasMaxLength(64);
            e.HasIndex(t => t.Token).IsUnique();
            e.HasIndex(t => new { t.ProjectId, t.Status });
            e.Property(t => t.WorkflowState).HasConversion(nullableJsonConverter, nullableJsonComparer);
            e.Property(t => t.Result).HasConversion(nullableJsonConverter, nullableJsonComparer);
            if (isNpgsql)
            {
                e.Property(t => t.WorkflowState).HasColumnType("jsonb");
                e.Property(t => t.Result).HasColumnType("jsonb");
            }

            e.HasOne(t => t.Project).WithMany(p => p.Tickets).HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Question>(e =>
        {
            e.ToTable("questions");
            e.HasIndex(q => q.TicketId);
            e.HasIndex(q => q.RunId);
            e.HasOne(q => q.Ticket).WithMany(t => t.Questions).HasForeignKey(q => q.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Comment>(e =>
        {
            e.ToTable("comments");
            e.Property(c => c.Author).HasConversion(new SnakeCaseEnumConverter<CommentAuthor>()).HasMaxLength(16);
            e.HasIndex(c => c.TicketId);
            e.HasOne(c => c.Ticket).WithMany(t => t.Comments).HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Run>(e =>
        {
            e.ToTable("runs");
            e.Property(r => r.Kind).HasConversion(new SnakeCaseEnumConverter<RunKind>()).HasMaxLength(16);
            e.Property(r => r.Status).HasConversion(new SnakeCaseEnumConverter<RunStatus>()).HasMaxLength(16);
            e.HasIndex(r => r.TicketId);
            e.HasIndex(r => r.Status);
            // Partial unique index: at most one pending/running run per ticket.
            e.HasIndex(r => r.TicketId)
                .HasDatabaseName("ix_runs_one_active_per_ticket")
                .IsUnique()
                .HasFilter("status IN ('pending', 'running')");
            e.HasOne(r => r.Ticket).WithMany(t => t.Runs).HasForeignKey(r => r.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunEvent>(e =>
        {
            e.ToTable("run_events");
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.RunId, x.Seq }).IsUnique();
            e.Property(x => x.Payload).HasConversion(jsonConverter, jsonComparer);
            if (isNpgsql)
            {
                e.Property(x => x.Payload).HasColumnType("jsonb");
            }

            e.HasOne(x => x.Run).WithMany(r => r.Events).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProgressNote>(e =>
        {
            e.ToTable("progress_notes");
            e.HasIndex(p => new { p.TicketId, p.CreatedAt });
            e.HasOne(p => p.Ticket).WithMany(t => t.ProgressNotes).HasForeignKey(p => p.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        ApplySnakeCaseNames(modelBuilder);
    }

    private static string? JsonText(JsonDocument? d) => d?.RootElement.GetRawText();

    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName()!));
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                fk.SetConstraintName(ToSnakeCase(fk.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()!));
            }
        }
    }

    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var prevIsBoundary = i > 0 && name[i - 1] != '_';
                var startsWord = i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (prevIsBoundary && startsWord)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

public sealed class SnakeCaseEnumConverter<T>() : ValueConverter<T, string>(
    v => EnumNames.ToWire(v),
    s => EnumNames.Parse<T>(s))
    where T : struct, Enum;
