using System.ComponentModel;

using ModelContextProtocol.Server;

namespace MafStarterPack.McpTodo;

// The model never sees this code. It chooses a tool only from each tool's name, description and
// parameter descriptions, and it reads the returned text to decide what to do next. So each
// description says what the tool does, when to use it, when NOT to, and what it returns, and
// every error message tells the model how to recover.
[McpServerToolType]
public class TodoTool(ITodoRepository todo, ILogger<TodoTool> logger)
{
    private const string NotFoundHint = "Call get_todo_items to see the current IDs.";

    [McpServerTool(Name = "add_todo_item", Title = "Add a to-do item",
                   ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Adds one new item to the user's to-do list.
        Use when the user asks to add, create, remember or note down a task.
        Call it once per item; to add several items, call it several times.
        Do not use it to change an existing item; use update_todo_item for that.
        Returns the new item's ID and text.
        """)]
    public async Task<string> AddTodoItemAsync(
        [Description("The task text, exactly as the user wants it written, e.g. \"buy milk\". Must not be empty.")]
        string todoItemText
    )
    {
        if (string.IsNullOrWhiteSpace(todoItemText))
        {
            return "Not added: the task text is empty. Ask the user what the task should say.";
        }

        var todoItem = new TodoItem { Text = todoItemText.Trim() };
        await todo.AddTodoItemAsync(todoItem).ConfigureAwait(false);

        logger.LogInformation("Todo item added: '{todoItemText}' (ID: {Id})", todoItem.Text, todoItem.Id);

        return $"Added item {todoItem.Id}: '{todoItem.Text}'.";
    }

    [McpServerTool(Name = "get_todo_items", Title = "Get a list of to-do items",
                   ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Lists every item on the user's to-do list with its ID, text and whether it is completed.
        Use when the user asks to see their list, and ALWAYS call it first before updating,
        completing or deleting an item, to find the item's ID. Never guess an ID.
        Changes nothing.
        """)]
    public async Task<IEnumerable<string>> GetTodoItemsAsync()
    {
        var todoItems = await todo.GetAllTodoItemsAsync().ConfigureAwait(false);

        logger.LogInformation("Retrieved {Count} todo items.", todoItems.Count());

        return todoItems.Any()
               ? todoItems.Select(p => $"ID: {p.Id}, Text: {p.Text}, Completed: {p.IsCompleted}")
               : [ "The to-do list is empty." ];
    }

    [McpServerTool(Name = "update_todo_item", Title = "Change a to-do item's text",
                   ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Replaces the text of one existing item. Use when the user asks to rename, reword, edit
        or correct an item. Do not use it to mark an item done (use complete_todo_item) or to
        remove it (use delete_todo_item). Get the ID from get_todo_items first.
        Returns the item's ID with its old and new text.
        """)]
    public async Task<string> UpdateTodoItemAsync(
        [Description("ID of the item to change, taken from get_todo_items.")] int id,
        [Description("The complete new text for the item, replacing the old text. Must not be empty.")]
        string todoItemText
    )
    {
        if (string.IsNullOrWhiteSpace(todoItemText))
        {
            return "Not updated: the new text is empty. Ask the user what the item should say.";
        }

        var todoItem = new TodoItem { Id = id, Text = todoItemText.Trim() };
        var updated = await todo.UpdateTodoItemAsync(todoItem).ConfigureAwait(false);
        if (updated is null)
        {
            logger.LogWarning("Todo item with ID '{id}' not found.", id);

            return $"Not updated: there is no item with ID {id}. {NotFoundHint}";
        }

        logger.LogInformation("Updated todo item: '{id}' with text: '{text}'", id, todoItem.Text);

        return $"Updated item {id}: the text is now '{todoItem.Text}'.";
    }

    [McpServerTool(Name = "complete_todo_item", Title = "Mark a to-do item as done",
                   ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Marks one existing item as completed. The item stays on the list, shown as done.
        Use when the user says they did, finished, completed or ticked off a task.
        Do not use it when the user wants the item gone; use delete_todo_item for that.
        Get the ID from get_todo_items first. Returns the item's ID and text.
        """)]
    public async Task<string> CompleteTodoItemAsync(
        [Description("ID of the item to mark as done, taken from get_todo_items.")] int id
    )
    {
        var todoItem = new TodoItem() { Id = id, IsCompleted = true };
        var completed = await todo.CompleteTodoItemAsync(todoItem).ConfigureAwait(false);
        if (completed is null)
        {
            logger.LogWarning("Todo item with ID '{id}' not found.", id);

            return $"Not completed: there is no item with ID {id}. {NotFoundHint}";
        }

        logger.LogInformation("Completed todo item: '{id}'", id);

        return $"Marked item {id} as done: '{completed.Text}'.";
    }

    [McpServerTool(Name = "delete_todo_item", Title = "Delete a to-do item permanently",
                   ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Permanently removes one item from the list. This cannot be undone.
        Use only when the user explicitly asks to remove, delete or get rid of an item.
        Do not use it when the user finished a task; use complete_todo_item for that.
        Get the ID from get_todo_items first. Returns the deleted item's ID and text.
        """)]
    public async Task<string> DeleteTodoItemAsync(
        [Description("ID of the item to delete, taken from get_todo_items.")] int id
    )
    {
        var deleted = await todo.DeleteTodoItemAsync(id).ConfigureAwait(false);
        if (deleted is null)
        {
            logger.LogWarning("Todo item with ID '{id}' not found.", id);

            return $"Not deleted: there is no item with ID {id}. {NotFoundHint}";
        }

        logger.LogInformation("Deleted todo item: '{id}'", id);

        return $"Deleted item {id}: '{deleted.Text}'.";
    }
}
