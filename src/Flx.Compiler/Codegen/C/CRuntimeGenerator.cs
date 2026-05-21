using System.Text;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Codegen.C;

internal sealed class CRuntimeGenerator
{
    public string GenerateHeader(
        CompilationModel model,
        Func<ComponentSymbol, bool>? includeComponent = null,
        Func<PrefabSymbol, bool>? includePrefab = null)
    {
        var builder = new StringBuilder();
        var components = model.ComponentsByFullName.Values
            .Where(includeComponent ?? (_ => true))
            .OrderBy(component => component.FullName, StringComparer.Ordinal)
            .ToArray();
        var prefabs = model.PrefabsByFullName.Values
            .Where(includePrefab ?? (_ => true))
            .OrderBy(prefab => prefab.FullName, StringComparer.Ordinal)
            .ToArray();

        builder.AppendLine("#ifndef FLX_RUNTIME_G_H");
        builder.AppendLine("#define FLX_RUNTIME_G_H");
        builder.AppendLine();
        builder.AppendLine("#include <stddef.h>");
        builder.AppendLine("#include <stdint.h>");
        builder.AppendLine();
        builder.AppendLine("typedef size_t usize;");
        builder.AppendLine("typedef int32_t i32;");
        builder.AppendLine("typedef uint32_t u32;");
        builder.AppendLine();
        builder.AppendLine("typedef struct flx_string {");
        builder.AppendLine("    char *data;");
        builder.AppendLine("    usize length;");
        builder.AppendLine("    usize capacity;");
        builder.AppendLine("    u32 flags;");
        builder.AppendLine("} flx_string;");
        builder.AppendLine();
        builder.AppendLine("typedef struct flx_array_string {");
        builder.AppendLine("    flx_string *items;");
        builder.AppendLine("    usize count;");
        builder.AppendLine("    usize capacity;");
        builder.AppendLine("} flx_array_string;");
        builder.AppendLine();
        builder.AppendLine("flx_string flx_string_empty(void);");
        builder.AppendLine("flx_string flx_string_from_static(const char *data, usize len);");
        builder.AppendLine("flx_string flx_string_from_cstr_borrowed(const char *value);");
        builder.AppendLine("flx_string flx_string_clone(const flx_string *s);");
        builder.AppendLine("void flx_string_assign(flx_string *dst, const flx_string *src);");
        builder.AppendLine("void flx_string_destroy(flx_string *s);");
        builder.AppendLine("const char *flx_string_c_str(const flx_string *s);");
        builder.AppendLine("usize flx_string_length(const flx_string *s);");
        builder.AppendLine();
        builder.AppendLine("void flx_array_string_init(flx_array_string *a);");
        builder.AppendLine("void flx_array_string_push(flx_array_string *a, flx_string value);");
        builder.AppendLine("usize flx_array_string_length(const flx_array_string *a);");
        builder.AppendLine("flx_string *flx_array_string_at(flx_array_string *a, usize index);");
        builder.AppendLine("void flx_array_string_destroy(flx_array_string *a);");
        builder.AppendLine("void flx_array_string_init_from_c_argv(flx_array_string *a, int argc, char **argv);");
        builder.AppendLine("void flx_array_string_destroy_borrowed(flx_array_string *a);");
        builder.AppendLine();

        foreach (var component in components)
        {
            builder.AppendLine($"typedef struct {CTypeNames.ComponentType(component.FullName)} {{");
            foreach (var field in component.Fields)
                builder.AppendLine($"    {CTypeNames.MapType(field.Type, model)} {field.Name};");
            builder.AppendLine($"}} {CTypeNames.ComponentType(component.FullName)};");
            builder.AppendLine();
        }

        foreach (var prefab in prefabs)
        {
            builder.AppendLine($"typedef struct {CTypeNames.PrefabType(prefab.FullName)} {{");
            builder.AppendLine("    usize id;");
            foreach (var component in prefab.FlattenedComponents)
                builder.AppendLine($"    {CTypeNames.ComponentType(component.FullName)} {CTypeNames.SafeIdentifier(component.FullName)};");
            builder.AppendLine($"}} {CTypeNames.PrefabType(prefab.FullName)};");
            builder.AppendLine();
            builder.AppendLine($"typedef struct {CTypeNames.ViewType(prefab.FullName)} {{");
            builder.AppendLine($"    {CTypeNames.PrefabType(prefab.FullName)} *ptr;");
            builder.AppendLine($"}} {CTypeNames.ViewType(prefab.FullName)};");
            builder.AppendLine();
        }

        builder.AppendLine("typedef struct flx_world {");
        if (prefabs.Length == 0)
            builder.AppendLine("    int _unused;");
        foreach (var prefab in prefabs)
        {
            builder.AppendLine($"    {CTypeNames.PrefabType(prefab.FullName)} *{CTypeNames.StorageField(prefab.FullName)};");
            builder.AppendLine($"    usize {CTypeNames.CountField(prefab.FullName)};");
            builder.AppendLine($"    usize {CTypeNames.CapacityField(prefab.FullName)};");
        }
        builder.AppendLine("} flx_world;");
        builder.AppendLine();
        builder.AppendLine("void flx_world_init(flx_world *world);");
        builder.AppendLine("void flx_world_destroy(flx_world *world);");

        foreach (var prefab in prefabs)
        {
            builder.AppendLine($"{CTypeNames.ViewType(prefab.FullName)} {CTypeNames.CreateFunction(prefab.FullName)}(flx_world *world);");
            builder.AppendLine($"{CTypeNames.ViewType(prefab.FullName)} {CTypeNames.GetFunction(prefab.FullName)}(flx_world *world, usize index);");
        }

        builder.AppendLine();
        builder.AppendLine("#endif");
        return builder.ToString();
    }

    public string GenerateSource(CompilationModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#include \"flx_runtime.g.h\"");
        builder.AppendLine();
        builder.AppendLine("#include <stdlib.h>");
        builder.AppendLine("#include <string.h>");
        builder.AppendLine();
        builder.AppendLine("#define FLX_STRING_OWNED 1u");
        builder.AppendLine("#define FLX_STRING_STATIC 2u");
        builder.AppendLine("#define FLX_STRING_BORROWED 4u");
        builder.AppendLine();
        builder.AppendLine("flx_string flx_string_empty(void) {");
        builder.AppendLine("    flx_string s = { (char *)\"\", 0, 0, FLX_STRING_STATIC };");
        builder.AppendLine("    return s;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("flx_string flx_string_from_static(const char *data, usize len) {");
        builder.AppendLine("    flx_string s = { (char *)data, len, len, FLX_STRING_STATIC };");
        builder.AppendLine("    return s;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("flx_string flx_string_from_cstr_borrowed(const char *value) {");
        builder.AppendLine("    if (value == NULL) return flx_string_empty();");
        builder.AppendLine("    usize len = (usize)strlen(value);");
        builder.AppendLine("    flx_string s = { (char *)value, len, len, FLX_STRING_BORROWED };");
        builder.AppendLine("    return s;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("flx_string flx_string_clone(const flx_string *s) {");
        builder.AppendLine("    char *copy = (char *)malloc(s->length + 1);");
        builder.AppendLine("    if (copy == NULL) abort();");
        builder.AppendLine("    if (s->length > 0) memcpy(copy, s->data, s->length);");
        builder.AppendLine("    copy[s->length] = '\\0';");
        builder.AppendLine("    flx_string result = { copy, s->length, s->length, FLX_STRING_OWNED };");
        builder.AppendLine("    return result;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_string_assign(flx_string *dst, const flx_string *src) {");
        builder.AppendLine("    if (dst == src) return;");
        builder.AppendLine("    flx_string copy = flx_string_clone(src);");
        builder.AppendLine("    flx_string_destroy(dst);");
        builder.AppendLine("    *dst = copy;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_string_destroy(flx_string *s) {");
        builder.AppendLine("    if ((s->flags & FLX_STRING_OWNED) != 0) free(s->data);");
        builder.AppendLine("    *s = flx_string_empty();");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("const char *flx_string_c_str(const flx_string *s) {");
        builder.AppendLine("    return s->data == NULL ? \"\" : s->data;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("usize flx_string_length(const flx_string *s) {");
        builder.AppendLine("    return s->length;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_array_string_init(flx_array_string *a) {");
        builder.AppendLine("    a->items = NULL;");
        builder.AppendLine("    a->count = 0;");
        builder.AppendLine("    a->capacity = 0;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_array_string_push(flx_array_string *a, flx_string value) {");
        builder.AppendLine("    if (a->count == a->capacity) {");
        builder.AppendLine("        usize next = a->capacity == 0 ? 8 : a->capacity * 2;");
        builder.AppendLine("        flx_string *items = (flx_string *)realloc(a->items, next * sizeof(flx_string));");
        builder.AppendLine("        if (items == NULL) abort();");
        builder.AppendLine("        a->items = items;");
        builder.AppendLine("        a->capacity = next;");
        builder.AppendLine("    }");
        builder.AppendLine("    a->items[a->count++] = value;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("usize flx_array_string_length(const flx_array_string *a) {");
        builder.AppendLine("    return a->count;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("flx_string *flx_array_string_at(flx_array_string *a, usize index) {");
        builder.AppendLine("    if (index >= a->count) abort();");
        builder.AppendLine("    return &a->items[index];");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_array_string_destroy(flx_array_string *a) {");
        builder.AppendLine("    for (usize i = 0; i < a->count; ++i) flx_string_destroy(&a->items[i]);");
        builder.AppendLine("    free(a->items);");
        builder.AppendLine("    flx_array_string_init(a);");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_array_string_init_from_c_argv(flx_array_string *a, int argc, char **argv) {");
        builder.AppendLine("    flx_array_string_init(a);");
        builder.AppendLine("    if (argc <= 0) return;");
        builder.AppendLine("    a->items = (flx_string *)malloc((usize)argc * sizeof(flx_string));");
        builder.AppendLine("    if (a->items == NULL) abort();");
        builder.AppendLine("    a->count = (usize)argc;");
        builder.AppendLine("    a->capacity = (usize)argc;");
        builder.AppendLine("    for (int i = 0; i < argc; ++i) a->items[i] = flx_string_from_cstr_borrowed(argv[i]);");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_array_string_destroy_borrowed(flx_array_string *a) {");
        builder.AppendLine("    free(a->items);");
        builder.AppendLine("    flx_array_string_init(a);");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_world_init(flx_world *world) {");
        foreach (var prefab in model.PrefabsByFullName.Values.OrderBy(prefab => prefab.FullName, StringComparer.Ordinal))
        {
            builder.AppendLine($"    world->{CTypeNames.StorageField(prefab.FullName)} = NULL;");
            builder.AppendLine($"    world->{CTypeNames.CountField(prefab.FullName)} = 0;");
            builder.AppendLine($"    world->{CTypeNames.CapacityField(prefab.FullName)} = 0;");
        }
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("void flx_world_destroy(flx_world *world) {");
        foreach (var prefab in model.PrefabsByFullName.Values.OrderBy(prefab => prefab.FullName, StringComparer.Ordinal))
        {
            builder.AppendLine($"    for (usize i = 0; i < world->{CTypeNames.CountField(prefab.FullName)}; ++i) {{");
            foreach (var component in prefab.FlattenedComponents)
            {
                foreach (var field in component.Fields.Where(field => field.Type == "string"))
                    builder.AppendLine($"        flx_string_destroy(&world->{CTypeNames.StorageField(prefab.FullName)}[i].{CTypeNames.SafeIdentifier(component.FullName)}.{field.Name});");
            }
            builder.AppendLine("    }");
            builder.AppendLine($"    free(world->{CTypeNames.StorageField(prefab.FullName)});");
        }
        builder.AppendLine("}");
        builder.AppendLine();

        foreach (var prefab in model.PrefabsByFullName.Values.OrderBy(prefab => prefab.FullName, StringComparer.Ordinal))
            AppendPrefabFunctions(builder, prefab);

        return builder.ToString();
    }

    private static void AppendPrefabFunctions(StringBuilder builder, PrefabSymbol prefab)
    {
        builder.AppendLine($"{CTypeNames.ViewType(prefab.FullName)} {CTypeNames.CreateFunction(prefab.FullName)}(flx_world *world) {{");
        builder.AppendLine($"    if (world->{CTypeNames.CountField(prefab.FullName)} == world->{CTypeNames.CapacityField(prefab.FullName)}) {{");
        builder.AppendLine($"        usize next = world->{CTypeNames.CapacityField(prefab.FullName)} == 0 ? 8 : world->{CTypeNames.CapacityField(prefab.FullName)} * 2;");
        builder.AppendLine($"        {CTypeNames.PrefabType(prefab.FullName)} *items = ({CTypeNames.PrefabType(prefab.FullName)} *)realloc(world->{CTypeNames.StorageField(prefab.FullName)}, next * sizeof({CTypeNames.PrefabType(prefab.FullName)}));");
        builder.AppendLine("        if (items == NULL) abort();");
        builder.AppendLine($"        world->{CTypeNames.StorageField(prefab.FullName)} = items;");
        builder.AppendLine($"        world->{CTypeNames.CapacityField(prefab.FullName)} = next;");
        builder.AppendLine("    }");
        builder.AppendLine($"    {CTypeNames.PrefabType(prefab.FullName)} *item = &world->{CTypeNames.StorageField(prefab.FullName)}[world->{CTypeNames.CountField(prefab.FullName)}];");
        builder.AppendLine($"    item->id = world->{CTypeNames.CountField(prefab.FullName)} + 1;");
        foreach (var component in prefab.FlattenedComponents)
        {
            foreach (var field in component.Fields.Where(field => field.Type == "string"))
            {
                if (field.DefaultValue is { } defaultValue)
                    builder.AppendLine($"    item->{CTypeNames.SafeIdentifier(component.FullName)}.{field.Name} = flx_string_from_static({defaultValue}, {StringLiteralLength(defaultValue)});");
                else
                    builder.AppendLine($"    item->{CTypeNames.SafeIdentifier(component.FullName)}.{field.Name} = flx_string_empty();");
            }
        }
        builder.AppendLine($"    world->{CTypeNames.CountField(prefab.FullName)}++;");
        builder.AppendLine($"    {CTypeNames.ViewType(prefab.FullName)} view = {{ item }};");
        builder.AppendLine("    return view;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"{CTypeNames.ViewType(prefab.FullName)} {CTypeNames.GetFunction(prefab.FullName)}(flx_world *world, usize index) {{");
        builder.AppendLine($"    if (index >= world->{CTypeNames.CountField(prefab.FullName)}) abort();");
        builder.AppendLine($"    {CTypeNames.ViewType(prefab.FullName)} view = {{ &world->{CTypeNames.StorageField(prefab.FullName)}[index] }};");
        builder.AppendLine("    return view;");
        builder.AppendLine("}");
        builder.AppendLine();
    }

    private static int StringLiteralLength(string literal)
    {
        var length = 0;
        for (var i = 1; i < literal.Length - 1; i++)
        {
            if (literal[i] == '\\' && i + 1 < literal.Length - 1)
            {
                i++;
                length++;
                continue;
            }

            length++;
        }

        return length;
    }
}
