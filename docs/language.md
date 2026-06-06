# FLX Language Notes

FLX is an experimental C-like language that lowers to generated C. The current surface is deliberately small and favors C interop and readable generated output.

## Components and Prefabs

Components are data declarations:

```flx
component AgentData {
    i32 x = 0;
    i32 y = 0;
    usize energy = 10;
    string name = "agent";
}
```

Prefabs flatten one or more components into an object shape:

```flx
prefab Agent {
    flatten AgentData;
}
```

`create Agent` appends an object to generated world storage and returns a temporary view:

```flx
Agent agent = create Agent;
agent.x = agent.x + 1;
```

Prefab values are reference-like temporary views. They are not persistent object references and should not be stored in component data.

## Field Types

Implemented component field types:

- `string`
- `i32`
- `usize`

String fields use `flx_string` storage. Assignment clones/copies string values so component storage does not point at destroyed temporaries.

Numeric fields lower to direct C fields in generated world storage. Defaults must be integer literals for now:

```flx
component Stats {
    i32 health = 100;
    usize score = 0;
}
```

Known limitations:

- No `f32`, `f64`, vectors, arrays, or nested component values yet.
- Numeric field defaults are limited to integer literals.
- Mutating prefab fields makes a scheduled prefab function serial under the current conservative parallel analysis.

## Methods

Functions whose first parameter is a prefab can be called with method syntax:

```flx
void Print(Agent agent) {
    // ...
}

agent.Print();
```

The compiler lowers method calls to regular C functions with the prefab view passed explicitly.
