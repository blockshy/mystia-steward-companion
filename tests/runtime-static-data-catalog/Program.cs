using MystiaStewardCompanion.Save;

try
{
    AssertMappedRecipeDependencyClosure();
    AssertInputsAreFrozenAndUnrelatedContentIsExcluded();
    AssertInvalidIdentityFailsClosed();
    AssertBoundsFailClosed();

    Console.WriteLine(
        "PASS: runtime static data uses five mapping roots plus a strict, bounded direct recipe dependency closure.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertMappedRecipeDependencyClosure()
{
    var closure = RuntimeRecipeDependencyProjection.Build(
        new[] { 1, 2 },
        new[] { 10 },
        new[]
        {
            Recipe(100, 10, 1, 3, 3),
            Recipe(102, 11, 4),
            Recipe(101, 11, 3),
        });

    AssertSequenceEqual(new[] { 1, 2, 3, 4 }, closure.IngredientIds, "Ingredient closure is incorrect.");
    AssertSequenceEqual(new[] { 10, 11 }, closure.FoodIds, "Food closure is incorrect.");
    AssertSequenceEqual(
        new[] { 3, 4 },
        closure.DependencyIngredientIds,
        "Mapped ingredients were not separated from recipe-only dependencies.");
    AssertSequenceEqual(
        new[] { 11 },
        closure.DependencyFoodIds,
        "Mapped foods were not separated from recipe-only dependencies.");
    AssertSequenceEqual(
        new[] { 100, 101 },
        closure.IngredientSourceRecipeIds[3],
        "Ingredient dependency sources are not unique and deterministic.");
    AssertSequenceEqual(
        new[] { 102 },
        closure.IngredientSourceRecipeIds[4],
        "Ingredient dependency source was lost.");
    AssertSequenceEqual(
        new[] { 101, 102 },
        closure.FoodSourceRecipeIds[11],
        "Food dependency sources are not unique and deterministic.");
    AssertFalse(
        closure.IngredientSourceRecipeIds.ContainsKey(1),
        "A mapping-root ingredient was mislabeled as a recipe-only dependency.");
    AssertFalse(
        closure.FoodSourceRecipeIds.ContainsKey(10),
        "A mapping-root food was mislabeled as a recipe-only dependency.");
}

static void AssertInputsAreFrozenAndUnrelatedContentIsExcluded()
{
    var mappedIngredients = new[] { 1 };
    var mappedFoods = new[] { 10 };
    var recipeIngredients = new[] { 1, 3 };
    var recipes = new[] { new RuntimeRecipeDescriptor(100, 10, recipeIngredients, "Pot") };
    var closure = RuntimeRecipeDependencyProjection.Build(mappedIngredients, mappedFoods, recipes);

    mappedIngredients[0] = 999;
    mappedFoods[0] = 999;
    recipeIngredients[1] = 999;

    AssertSequenceEqual(new[] { 1, 3 }, closure.IngredientIds, "Ingredient closure retained mutable input state.");
    AssertSequenceEqual(new[] { 10 }, closure.FoodIds, "Food closure retained mutable input state.");
    AssertFalse(closure.IngredientIds.Contains(999), "Unrelated database content leaked into the closure.");
    AssertFalse(closure.FoodIds.Contains(999), "Unrelated database content leaked into the closure.");
}

static void AssertInvalidIdentityFailsClosed()
{
    AssertThrowsContains(
        () => RuntimeRecipeDependencyProjection.Build(new[] { 1, 1 }, new[] { 10 }, new[] { Recipe(100, 10, 1) }),
        "duplicate content ID 1",
        "Duplicate mapping IDs were accepted.");
    AssertThrowsContains(
        () => RuntimeRecipeDependencyProjection.Build(new[] { 1 }, new[] { 10 }, new[]
        {
            Recipe(100, 10, 1),
            Recipe(100, 10, 1),
        }),
        "duplicate recipe ID 100",
        "Duplicate recipe IDs were accepted.");
    AssertThrowsContains(
        () => RuntimeRecipeDependencyProjection.Build(new[] { 1 }, new[] { 10 }, new[] { Recipe(-1, 10, 1) }),
        "negative recipe ID -1",
        "Negative recipe IDs were accepted.");
    AssertThrowsContains(
        () => RuntimeRecipeDependencyProjection.Build(new[] { 1 }, new[] { 10 }, new[] { Recipe(100, -1, 1) }),
        "negative food ID -1",
        "Negative food dependencies were accepted.");
    AssertThrowsContains(
        () => RuntimeRecipeDependencyProjection.Build(new[] { 1 }, new[] { 10 }, new[] { Recipe(100, 10, -1) }),
        "negative ingredient ID -1",
        "Negative ingredient dependencies were accepted.");
    AssertThrowsContains(
        () => RuntimeRecipeDependencyProjection.Build(new[] { 1 }, new[] { 10 }, Array.Empty<RuntimeRecipeDescriptor>()),
        "invalid descriptor count 0",
        "An empty mapped recipe catalog was accepted.");
}

static void AssertBoundsFailClosed()
{
    AssertThrowsContains(
        () => RuntimeRecipeDependencyProjection.Build(
            Enumerable.Range(0, 4096).ToArray(),
            new[] { 10 },
            new[] { Recipe(100, 10, 4096) }),
        "ingredient catalog beyond the 4096-item limit",
        "A recipe dependency expanded the ingredient catalog beyond its limit.");
    AssertThrowsContains(
        () => RuntimeRecipeDependencyProjection.Build(
            new[] { 1 },
            new[] { 10 },
            new[] { new RuntimeRecipeDescriptor(100, 10, Enumerable.Repeat(1, 16385).ToArray(), "Pot") }),
        "16384-ingredient-reference limit",
        "The total recipe ingredient-reference bound was not enforced.");
}

static RuntimeRecipeDescriptor Recipe(int recipeId, int foodId, params int[] ingredientIds)
{
    return new RuntimeRecipeDescriptor(recipeId, foodId, ingredientIds, "Pot");
}

static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}].");
    }
}

static void AssertFalse(bool condition, string message)
{
    if (condition) throw new InvalidOperationException(message);
}

static void AssertThrowsContains(Action action, string expectedMessage, string failureMessage)
{
    try
    {
        action();
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
        return;
    }

    throw new InvalidOperationException(failureMessage);
}
