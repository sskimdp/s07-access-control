using TrackerApi;

// Учебный "Внутренний трекер обращений" для семинара С07 (Access-control cycle).
//
// ВНИМАНИЕ (учебный объект): приложение СОДЕРЖИТ НАМЕРЕННЫЕ ДЕФЕКТЫ КОНТРОЛЯ
// ДОСТУПА трех классов из Л07. Легенда: "продукт работает, но служба
// безопасности нашла, что пользователи видят лишнее". Маркеры "REVIEW-n"
// отмечают места, которые стоит рассмотреть; класс дефекта и способ
// исправления в коде НЕ подписаны - это задача студента. Это не образец кода.
//
// Модель ролей: заявитель (Requester) видит свои обращения; исполнитель
// (Resolver) работает с обращениями своего подразделения; руководитель
// (Manager) видит все подразделение. Текущий пользователь - из заголовка
// X-User (роль и подразделение берутся из справочника).

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TrackerStore>();
var app = builder.Build();

// Определение текущего пользователя. Аутентификация не моделируется: логин
// берется из заголовка. Это упрощение объекта, а не дефект.
static User? Current(HttpRequest req, TrackerStore store)
{
    var login = req.Headers["X-User"].FirstOrDefault();
    return string.IsNullOrWhiteSpace(login) ? null : store.FindUser(login);
}

// Правило чтения обращения (единое для всех маршрутов, которые отдают данные
// обращения или связанных с ним объектов): заявитель читает только свои
// обращения, исполнитель и руководитель - обращения своего подразделения.
static bool CanRead(User me, Issue issue) => me.Role switch
{
    Role.Requester => issue.Author == me.Login,
    Role.Resolver or Role.Manager => issue.Department == me.Department,
    _ => false
};

// Правило закрытия обращения: два независимых ограничения. Роль - закрывают
// только исполнитель и руководитель (заявитель не закрывает даже свое
// обращение). Подразделение - только обращения своего подразделения.
static bool CanClose(User me, Issue issue) =>
    me.Role is Role.Resolver or Role.Manager && issue.Department == me.Department;

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Список своих обращений (штатный путь, здесь доступ ограничен корректно) -
// служит регрессионным ориентиром и главным путем, на фоне которого дефекты
// в других путях заметнее.
app.MapGet("/issues", (HttpRequest req, TrackerStore store) =>
{
    var me = Current(req, store);
    if (me is null) return Results.Unauthorized();

    IEnumerable<Issue> visible = me.Role switch
    {
        Role.Requester => store.Issues.Where(i => i.Author == me.Login),
        Role.Resolver  => store.Issues.Where(i => i.Department == me.Department),
        Role.Manager   => store.Issues.Where(i => i.Department == me.Department),
        _              => Enumerable.Empty<Issue>()
    };
    return Results.Ok(visible);
});

// Получение обращения по идентификатору.
//
// REVIEW-1 (разбирается на семинаре): эндпоинт отдает обращение по id.
// Рассмотрите, какие условия доступа проверяются перед выдачей.
app.MapGet("/issues/{id:int}", (int id, HttpRequest req, TrackerStore store) =>
{
    var me = Current(req, store);
    if (me is null) return Results.Unauthorized();

    var issue = store.FindIssue(id);
    // Д1: недоступное и несуществующее обращение неразличимы - оба дают 404.
    if (issue is null || !CanRead(me, issue)) return Results.NotFound();

    return Results.Ok(issue);
});

// Закрытие обращения.
//
// REVIEW-2: эндпоинт закрывает обращение. Рассмотрите, кто по замыслу должен
// иметь право на это действие и что проверяется на сервере.
app.MapPost("/issues/{id:int}/close", (int id, HttpRequest req, TrackerStore store) =>
{
    var me = Current(req, store);
    if (me is null) return Results.Unauthorized();

    var issue = store.FindIssue(id);
    // Д2: сначала право видеть объект (невидимое неотличимо от отсутствующего - 404),
    // затем право на действие над видимым объектом (403). Состояние меняется
    // только после обеих проверок.
    if (issue is null || !CanRead(me, issue)) return Results.NotFound();
    if (!CanClose(me, issue)) return Results.StatusCode(StatusCodes.Status403Forbidden);

    issue.Status = "closed";
    return Results.Ok(issue);
});

// Выдача вложения по идентификатору.
//
// REVIEW-3 (артефакт-результат самостоятельной работы): эндпоинт отдает
// вложение по его id. Рассмотрите, что проверяется перед выдачей и как это
// связано с доступом к родительскому обращению.
app.MapGet("/attachments/{id:int}", (int id, HttpRequest req, TrackerStore store) =>
{
    var me = Current(req, store);
    if (me is null) return Results.Unauthorized();

    var att = store.FindAttachment(id);
    // Д3: доступ к вложению определяется правом чтения родительского обращения.
    // Нет вложения, нет родителя или родитель недоступен - один и тот же ответ 404.
    var parent = att is null ? null : store.FindIssue(att.IssueId);
    if (att is null || parent is null || !CanRead(me, parent)) return Results.NotFound();

    return Results.Text(att.Content);
});

// Проверка существования обращения (используется интерфейсом для подсказок).
//
// REVIEW-4 (резерв): эндпоинт сообщает о существовании обращения. Рассмотрите,
// что разные коды ответа сообщают вызывающему о чужих обращениях.
app.MapGet("/issues/{id:int}/exists", (int id, HttpRequest req, TrackerStore store) =>
{
    var me = Current(req, store);
    if (me is null) return Results.Unauthorized();

    var issue = store.FindIssue(id);
    if (issue is null) return Results.NotFound();

    var mine = issue.Author == me.Login || issue.Department == me.Department;
    // REVIEW-4: рассмотрите, что коды ответов сообщают о чужом обращении.
    return mine ? Results.Ok(new { exists = true }) : Results.StatusCode(403);
});

app.Run();

public partial class Program;
