/*
    Tree3D — a 3D tree with thousands of independently fluttering leaves,
    driven by a basic wind simulation. Second game module for the FableC
    host (run: Engine.exe Tree3D.dll).

    What it demonstrates on top of the 2D demo:
      - full 3D transform hierarchy (raymath Matrix), propagated per depth
        level with ecs_parallel_for
      - one prefab (TreeNode) for both branches and leaves; the Animate
        system flutters ~4000 leaves in parallel chunks while branches sway,
        all reading a Wind singleton written by one task
      - raylib is OpenGL-based and therefore thread-affine: every GL call
        lives in TASK_MAIN tasks (Setup3D, Render3D) — the "bucket 3"
        external-call rule with a real API

    Environment:
      FABLEC_MAX_FRAMES   exit after N frames, uncapped FPS (default: vsync'd,
                          run until window closes)
*/
#include "raylib.h"
#include "raymath.h"
#include "rlgl.h"

#include <stdio.h>
#include <stdlib.h>
#include <math.h>

#include "ecs_meta.h"

#define BRANCH_FACTOR 3
#define TREE_DEPTH    9
#define LEAVES_PER_TIP 9   /* ~206k nodes: 29,524 branches + 177,147 leaves */
#define NO_DENSE 0xffffffffu
#define SERIAL_LEVEL_MAX 256   /* levels smaller than this propagate inline */

/* ------------------------------------------------------------------ */
/* Data                                                                */
/* ------------------------------------------------------------------ */

COMPONENT(LocalXf, { Vector3 pos; Vector3 rot; float scale; });
COMPONENT(WorldXf, { Matrix m; });
COMPONENT(GpuXf,   { float16 f; });   /* column-major GL layout, written during
                                         propagation so upload is a raw memcpy */
COMPONENT(Parent,  { Entity e; });
COMPONENT(NodeInfo, {
    int     is_leaf;
    float   phase;      /* per-node animation offset */
    float   len;        /* branch segment length (draw + child anchor) */
    Vector3 base_pos;   /* rest position within the parent frame */
    Vector3 base_rot;   /* rest orientation */
});

SINGLETON(Wind,     { Vector3 dir; float strength; float t; });
SINGLETON(Screen3D, {
    Camera3D cam;
    int      ready;
    int      nodraw;
    Mesh     leaf_mesh;      /* unit cube, drawn instanced */
    Mesh     branch_mesh;    /* unit cylinder (y in [0,1]), drawn instanced */
    Material inst_mat;       /* shared instancing shader */
    int      loc_tint_a;     /* per-pass color uniforms */
    int      loc_tint_b;
    unsigned int leaf_vbo;   /* persistent instance buffers, created once */
    unsigned int branch_vbo;
    float16 *branch_stage;   /* CPU staging for branch matrices (len/radius baked) */
    uint32_t branch_stage_cap;
});

/* leaves are spawned after all branches, so their rows form a contiguous
   tail of the register: [first_leaf, count) uploads straight from the
   WorldXf column with no per-frame gather */
SINGLETON(TreeInfo, { uint32_t first_leaf; });
SINGLETON(FrameStats, { uint32_t frame, max_frames; double total_ms; });
SINGLETON(LogSink,  { uint32_t total; });
SINGLETON(HierData, {
    uint32_t *order;
    uint32_t *parent_dense;
    uint32_t *depth;
    uint32_t *scratch;
    uint32_t  cap, count, levels;
    uint32_t  level_start[34];
});

PREFAB(TreeNode, LocalXf, WorldXf, GpuXf, Parent, NodeInfo);

/* ------------------------------------------------------------------ */
/* Helpers                                                             */
/* ------------------------------------------------------------------ */

static uint32_t hash_u32(uint32_t x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

static float frand(uint32_t seed)   /* [0, 1) */
{
    return (float)(hash_u32(seed) & 0xffffffu) / 16777216.0f;
}

static Matrix xf_local(const LocalXf *l)
{
    Matrix sr = MatrixMultiply(MatrixScale(l->scale, l->scale, l->scale),
                               MatrixRotateXYZ(l->rot));
    return MatrixMultiply(sr, MatrixTranslate(l->pos.x, l->pos.y, l->pos.z));
}

static Vector3 xf_translation(const Matrix *m)
{
    Vector3 v = { m->m12, m->m13, m->m14 };
    return v;
}

/* ------------------------------------------------------------------ */
/* Wind + animation                                                    */
/* ------------------------------------------------------------------ */

TASK(UpdateWind, WRITE(Wind, w))
{
    w->t = (float)ctx->time;

    /* gusts: layered sines, occasionally near-calm */
    float gust = 0.55f + 0.45f * sinf(w->t * 0.5f)
               + 0.25f * sinf(w->t * 1.7f + 1.3f)
               + 0.12f * sinf(w->t * 4.3f);
    w->strength = gust < 0.05f ? 0.05f : gust;

    float a = w->t * 0.1f;
    w->dir.x = cosf(a);
    w->dir.y = 0.0f;
    w->dir.z = sinf(a);
}

/*
    One system animates every node in parallel chunks: leaves jitter their
    anchor position (independent per-leaf phase and frequency), branches
    bend around their rest orientation, deeper in the gust direction.
*/
SYSTEM(TreeNode, Animate, WRITE(LocalXf, lt), READ(NodeInfo, ni), READ(Wind, wind))
{
    float t = wind->t;
    float s = wind->strength;

    if (ni->is_leaf) {
        float f = 5.0f + 4.0f * frand(ctx->self.slot);
        float p = ni->phase;
        lt->pos.x = ni->base_pos.x + sinf(t * f + p) * 0.10f * s;
        lt->pos.y = ni->base_pos.y + sinf(t * f * 0.8f + p * 1.7f) * 0.06f * s;
        lt->pos.z = ni->base_pos.z + cosf(t * f * 1.1f + p) * 0.10f * s;
    }
    else {
        lt->rot.x = ni->base_rot.x + sinf(t * 1.3f + ni->phase) * 0.035f * s * wind->dir.z;
        lt->rot.z = ni->base_rot.z - sinf(t * 1.1f + ni->phase * 0.7f) * 0.035f * s * wind->dir.x
                                   - 0.05f * s * wind->dir.x;
    }
}

/* ------------------------------------------------------------------ */
/* Hierarchy: depth buckets + level-parallel propagation               */
/* ------------------------------------------------------------------ */

static void hier_reserve(HierData *h, uint32_t n)
{
    if (n > h->cap) {
        uint32_t cap = h->cap ? h->cap : 4096;
        while (cap < n) cap *= 2;
        h->order = realloc(h->order, cap * sizeof(uint32_t));
        h->parent_dense = realloc(h->parent_dense, cap * sizeof(uint32_t));
        h->depth = realloc(h->depth, cap * sizeof(uint32_t));
        h->scratch = realloc(h->scratch, cap * sizeof(uint32_t));
        h->cap = cap;
    }
}

TASK(RebuildTree, QUERY(Parent, par), WRITE(HierData, h))
{
    uint32_t n = par_count;

    /* incremental: this scene never changes structurally after spawn, so
       the depth buckets stay valid. (Destroys/reparents would change the
       row count or need a dirty flag at the command barrier.) */
    if (h->levels > 0 && h->count == n) {
        return;
    }

    hier_reserve(h, n);
    h->count = n;
    if (n == 0) {
        h->levels = 0;
        return;
    }

    for (uint32_t i = 0; i < n; ++i) {
        int32_t pd = ecs_entity_index(ctx, par[i].e);
        h->parent_dense[i] = pd < 0 ? NO_DENSE : (uint32_t)pd;
    }

    uint32_t maxd = 0;
    for (uint32_t i = 0; i < n; ++i) {
        uint32_t d = 0, j = i;
        while (h->parent_dense[j] != NO_DENSE && d < 32) {
            j = h->parent_dense[j];
            d++;
        }
        h->depth[i] = d;
        if (d > maxd) maxd = d;
    }
    h->levels = maxd + 1;

    for (uint32_t L = 0; L <= h->levels; ++L) h->level_start[L] = 0;
    for (uint32_t i = 0; i < n; ++i) h->level_start[h->depth[i] + 1]++;
    for (uint32_t L = 1; L <= h->levels; ++L) h->level_start[L] += h->level_start[L - 1];

    for (uint32_t L = 0; L < h->levels; ++L) h->scratch[L] = h->level_start[L];
    for (uint32_t i = 0; i < n; ++i) h->order[h->scratch[h->depth[i]]++] = i;
}

typedef struct LvlArgs {
    WorldXf        *wm;
    GpuXf          *gpu;
    const LocalXf  *lt;
    const HierData *h;
    uint32_t        base;
} LvlArgs;

static void compute_level(SysCtx *sub, void *user)
{
    LvlArgs *a = (LvlArgs *)user;

    for (uint32_t k = sub->first; k < sub->last; ++k) {
        uint32_t i = a->h->order[a->base + k];
        Matrix local = xf_local(&a->lt[i]);
        uint32_t pd = a->h->parent_dense[i];
        a->wm[i].m = (pd == NO_DENSE) ? local : MatrixMultiply(local, a->wm[pd].m);
        a->gpu[i].f = MatrixToFloatV(a->wm[i].m);   /* GPU layout, transposed here
                                                       in parallel instead of on the
                                                       main thread at draw time */
    }
}

TASK(ComputeWorld, QUERY_MUT(WorldXf, wm), QUERY_MUT(GpuXf, gpu),
     QUERY(LocalXf, lt), READ(HierData, h))
{
    LvlArgs a = { wm, gpu, lt, h, 0 };

    for (uint32_t L = 0; L < h->levels; ++L) {
        a.base = h->level_start[L];
        uint32_t cnt = h->level_start[L + 1] - a.base;

        if (cnt < SERIAL_LEVEL_MAX) {
            /* dispatching jobs for a handful of nodes costs more than the work */
            SysCtx sub = *ctx;
            sub.first = 0;
            sub.last = cnt;
            compute_level(&sub, &a);
        }
        else {
            ecs_parallel_for(ctx, cnt, compute_level, &a);
        }
    }
}

/* Convention check, first frame only: a child's world origin must equal
   its local rest position transformed by its parent's world matrix. */
TASK(SanityCheck, QUERY(WorldXf, wm), QUERY(LocalXf, lt), QUERY(NodeInfo, nin),
     READ(HierData, h), READ(FrameStats, fs))
{
    if (fs->frame != 0 || h->levels < 3) {
        return;
    }

    uint32_t checked = 0, bad = 0;

    for (uint32_t k = h->level_start[1]; k < h->level_start[3]; ++k) {
        uint32_t i = h->order[k];
        uint32_t pd = h->parent_dense[i];
        Vector3 expect = Vector3Transform(lt[i].pos, wm[pd].m);
        Vector3 got = xf_translation(&wm[i].m);
        if (Vector3Distance(expect, got) > 0.001f) bad++;
        checked++;
    }

    uint32_t i2 = h->order[h->level_start[2]];
    Vector3 p2 = xf_translation(&wm[i2].m);
    ecs_log(ctx, "sanity: %u checked, %u bad; level-2 node at (%.2f, %.2f, %.2f)",
            checked, bad, p2.x, p2.y, p2.z);
    (void)nin;
}

/* ------------------------------------------------------------------ */
/* Spawning                                                            */
/* ------------------------------------------------------------------ */

#define MAX_TIPS 19683   /* BRANCH_FACTOR ^ TREE_DEPTH */

typedef struct SpawnState {
    Entity  *tips;
    float   *tip_len;
    uint32_t tip_count;
} SpawnState;

static void spawn_branch(SysCtx *ctx, SpawnState *st, Entity parent,
                         int depth, float plen, uint32_t seed)
{
    float len = plen * 0.72f;

    for (int k = 0; k < BRANCH_FACTOR; ++k) {
        uint32_t s = hash_u32(seed * 7u + (uint32_t)k * 131u + 17u);

        Vector3 pos = { 0.0f, plen, 0.0f };   /* anchored at the parent's tip */
        Vector3 rot = {
            (0.35f + 0.35f * frand(s)) * (frand(s ^ 3u) < 0.5f ? 1.0f : -1.0f),
            (float)k * (2.0f * PI / BRANCH_FACTOR) + frand(s ^ 9u) * 0.8f,
            (0.35f + 0.35f * frand(s ^ 5u)) * (frand(s ^ 7u) < 0.5f ? 1.0f : -1.0f)
        };

        Entity e = ecs_create(ctx, TreeNode);
        ecs_init(ctx, e, Parent, { parent });
        ecs_init(ctx, e, LocalXf, { pos, rot, 1.0f });
        ecs_init(ctx, e, NodeInfo, { 0, frand(s ^ 21u) * 6.28f, len, pos, rot });

        if (depth > 1) {
            spawn_branch(ctx, st, e, depth - 1, len, s);
        }
        else if (st->tip_count < MAX_TIPS) {
            st->tips[st->tip_count] = e;
            st->tip_len[st->tip_count] = len;
            st->tip_count++;
        }
    }
}

TASK(SpawnTree, WRITE(FrameStats, fs), WRITE(TreeInfo, ti))
{
    float trunk_len = 4.0f;

    /* phase 1: every branch, depth-first */
    Entity root = ecs_create(ctx, TreeNode);
    ecs_init(ctx, root, Parent, { ECS_NULL_ENTITY });
    ecs_init(ctx, root, LocalXf, { { 0, 0, 0 }, { 0, 0, 0 }, 1.0f });
    ecs_init(ctx, root, NodeInfo, { 0, 0.0f, trunk_len, { 0, 0, 0 }, { 0, 0, 0 } });

    SpawnState st;
    st.tips = malloc(MAX_TIPS * sizeof(Entity));
    st.tip_len = malloc(MAX_TIPS * sizeof(float));
    st.tip_count = 0;

    spawn_branch(ctx, &st, root, TREE_DEPTH, trunk_len, 12345u);

    uint32_t branches = 1;
    uint32_t tips = 1;
    for (int d = 0; d < TREE_DEPTH; ++d) {
        tips *= BRANCH_FACTOR;
        branches += tips;
    }
    ti->first_leaf = branches;   /* rows are appended in creation order */

    /* phase 2: all leaves, so they form the contiguous tail of the register */
    for (uint32_t t = 0; t < st.tip_count; ++t) {
        float len = st.tip_len[t];

        for (int j = 0; j < LEAVES_PER_TIP; ++j) {
            uint32_t ls = hash_u32(t * 2654435761u + (uint32_t)j * 97u);
            Vector3 lp = {
                (frand(ls) - 0.5f) * 0.5f,
                len * (0.4f + 0.6f * frand(ls ^ 2u)),
                (frand(ls ^ 4u) - 0.5f) * 0.5f
            };
            Vector3 lr = { frand(ls ^ 6u) * 3.1f, frand(ls ^ 7u) * 3.1f, 0 };
            float sz = 0.14f + 0.10f * frand(ls ^ 5u);   /* leaf size lives in scale,
                                                            so the world matrix is the
                                                            finished instance transform */

            Entity leaf = ecs_create(ctx, TreeNode);
            ecs_init(ctx, leaf, Parent, { st.tips[t] });
            ecs_init(ctx, leaf, LocalXf, { lp, lr, sz });
            ecs_init(ctx, leaf, NodeInfo, { 1, frand(ls ^ 8u) * 6.28f, 0.2f, lp, lr });
        }
    }

    ecs_log(ctx, "spawned tree: %u branches, %u leaves (first leaf row %u)",
            branches, st.tip_count * LEAVES_PER_TIP, ti->first_leaf);

    free(st.tips);
    free(st.tip_len);
    (void)fs;
}

/* ------------------------------------------------------------------ */
/* raylib tasks — all GL calls pinned to the main thread               */
/* ------------------------------------------------------------------ */

/* GLSL 330 instancing shader: per-instance transform attribute (raylib binds
   "instanceTransform" automatically), per-instance green tint from
   gl_InstanceID, one directional light from the normal. */
static const char *leaf_vs =
    "#version 330\n"
    "in vec3 vertexPosition;\n"
    "in vec3 vertexNormal;\n"
    "in mat4 instanceTransform;\n"
    "uniform mat4 mvp;\n"
    "uniform vec3 tintA;\n"
    "uniform vec3 tintB;\n"
    "out vec3 fragNormal;\n"
    "out vec3 tint;\n"
    "void main() {\n"
    "    gl_Position = mvp * instanceTransform * vec4(vertexPosition, 1.0);\n"
    "    fragNormal = normalize(mat3(instanceTransform) * vertexNormal);\n"
    "    float h = fract(sin(float(gl_InstanceID) * 12.9898) * 43758.5453);\n"
    "    tint = mix(tintA, tintB, h);\n"
    "}\n";

static const char *leaf_fs =
    "#version 330\n"
    "in vec3 fragNormal;\n"
    "in vec3 tint;\n"
    "out vec4 finalColor;\n"
    "void main() {\n"
    "    vec3 l = normalize(vec3(0.4, 0.8, 0.3));\n"
    "    float d = 0.55 + 0.45 * max(dot(normalize(fragNormal), l), 0.0);\n"
    "    finalColor = vec4(tint * d, 1.0);\n"
    "}\n";

/* Attach a persistent mat4 instance attribute to a mesh's VAO. The attrib
   pointers are VAO state, so this binding is done exactly once. */
static unsigned int setup_instance_vbo(const Mesh *mesh, int attrib_loc, uint32_t max_instances)
{
    rlEnableVertexArray(mesh->vaoId);

    unsigned int vbo = rlLoadVertexBuffer(NULL, (int)(max_instances * sizeof(float16)), true);

    for (unsigned int i = 0; i < 4; ++i) {   /* a mat4 attribute is 4 vec4 slots */
        rlEnableVertexAttribute((unsigned int)attrib_loc + i);
        rlSetVertexAttribute((unsigned int)attrib_loc + i, 4, RL_FLOAT, false,
                             sizeof(float16), (int)(i * 4 * sizeof(float)));
        rlSetVertexAttributeDivisor((unsigned int)attrib_loc + i, 1);
    }

    rlDisableVertexBuffer();
    rlDisableVertexArray();
    return vbo;
}

/* Instanced draw against the persistent buffer: enable shader, set mvp,
   draw the mesh's VAO. No per-call buffer churn. */
static void draw_instanced_persistent(const Mesh *mesh, const Material *mat, int instances)
{
    const Shader *shader = &mat->shader;

    rlEnableShader(shader->id);

    Matrix mat_view = rlGetMatrixModelview();
    Matrix mat_projection = rlGetMatrixProjection();
    Matrix mvp = MatrixMultiply(MatrixMultiply(rlGetMatrixTransform(), mat_view), mat_projection);
    rlSetUniformMatrix(shader->locs[SHADER_LOC_MATRIX_MVP], mvp);

    rlEnableVertexArray(mesh->vaoId);

    if (mesh->indices != NULL) {
        rlDrawVertexArrayElementsInstanced(0, mesh->triangleCount * 3, NULL, instances);
    }
    else {
        rlDrawVertexArrayInstanced(0, mesh->vertexCount, instances);
    }

    rlDisableVertexArray();
    rlDisableShader();
}

TASK_MAIN(Setup3D, WRITE(Screen3D, s), WRITE(FrameStats, fs))
{
    const char *max = getenv("FABLEC_MAX_FRAMES");
    fs->max_frames = max ? (uint32_t)atoi(max) : 0;

    /* FABLEC_FPS=0 runs uncapped; unset defaults to 60 (test mode is
       always uncapped) */
    const char *fps_env = getenv("FABLEC_FPS");
    int fps = fps_env ? atoi(fps_env) : 60;

    SetTraceLogLevel(LOG_WARNING);
    InitWindow(1280, 720, "FableC — wind tree (3D)");
    if (!fs->max_frames && fps > 0) {
        SetTargetFPS(fps);
    }

    s->cam.position = (Vector3){ 16.0f, 9.0f, 16.0f };
    s->cam.target = (Vector3){ 0.0f, 6.0f, 0.0f };
    s->cam.up = (Vector3){ 0.0f, 1.0f, 0.0f };
    s->cam.fovy = 45.0f;
    s->cam.projection = CAMERA_PERSPECTIVE;
    s->ready = 1;
    s->nodraw = getenv("FABLEC_NODRAW") != NULL;   /* profiling: skip scene submission */

    s->leaf_mesh = GenMeshCube(1.0f, 1.0f, 1.0f);
    s->branch_mesh = GenMeshCylinder(1.0f, 1.0f, 6);
    s->inst_mat = LoadMaterialDefault();
    s->inst_mat.shader = LoadShaderFromMemory(leaf_vs, leaf_fs);
    s->inst_mat.shader.locs[SHADER_LOC_MATRIX_MODEL] =
        GetShaderLocationAttrib(s->inst_mat.shader, "instanceTransform");
    s->loc_tint_a = GetShaderLocation(s->inst_mat.shader, "tintA");
    s->loc_tint_b = GetShaderLocation(s->inst_mat.shader, "tintB");

    /* persistent instance VBOs, attached to each mesh's VAO once — per frame
       we only rlUpdateVertexBuffer, no allocation or buffer recreation */
    uint32_t max_branches = 1, tips = 1;
    for (int d = 0; d < TREE_DEPTH; ++d) {
        tips *= BRANCH_FACTOR;
        max_branches += tips;
    }
    uint32_t max_leaves = tips * LEAVES_PER_TIP + 64;

    int loc = s->inst_mat.shader.locs[SHADER_LOC_MATRIX_MODEL];
    s->leaf_vbo = setup_instance_vbo(&s->leaf_mesh, loc, max_leaves);
    s->branch_vbo = setup_instance_vbo(&s->branch_mesh, loc, max_branches + 64);

    printf("[tree3d] window up; max frames: %u%s\n",
           fs->max_frames, fs->max_frames ? "" : " (run until window closes)");
}

TASK_MAIN(Render3D, WRITE(Screen3D, s), QUERY(WorldXf, xf), QUERY(GpuXf, gpu),
          QUERY(NodeInfo, ni), READ(HierData, h), READ(Wind, wind), READ(TreeInfo, ti))
{
    if (!s->ready) {
        return;
    }

    if (WindowShouldClose()) {
        ecs_quit(ctx);
        return;
    }

    float a = (float)ctx->time * 0.15f;
    float r = 17.0f;
    s->cam.position = (Vector3){ cosf(a) * r, 8.5f, sinf(a) * r };

    BeginDrawing();
    ClearBackground((Color){ 18, 20, 30, 255 });
    BeginMode3D(s->cam);

    DrawGrid(24, 1.0f);

    uint32_t n = xf_count < h->count ? xf_count : h->count;
    uint32_t first_leaf = ti->first_leaf < n ? ti->first_leaf : n;
    if (s->nodraw) {
        n = 0;
        first_leaf = 0;
    }

    if (first_leaf > s->branch_stage_cap) {
        uint32_t cap = s->branch_stage_cap ? s->branch_stage_cap : 4096;
        while (cap < first_leaf) cap *= 2;
        s->branch_stage = realloc(s->branch_stage, cap * sizeof(float16));
        s->branch_stage_cap = cap;
    }

    /* branches: bake length/radius into the instance matrix, stage in GPU
       layout, one buffer update */
    for (uint32_t i = 0; i < first_leaf; ++i) {
        float len = ni[i].len;
        float r = 0.025f + 0.045f * len;
        s->branch_stage[i] = MatrixToFloatV(MatrixMultiply(MatrixScale(r, len, r), xf[i].m));
    }

    float brown_a[3] = { 0.45f, 0.32f, 0.20f }, brown_b[3] = { 0.55f, 0.42f, 0.28f };
    float green_a[3] = { 0.12f, 0.55f, 0.22f }, green_b[3] = { 0.24f, 0.95f, 0.30f };

    if (first_leaf > 0) {
        rlUpdateVertexBuffer(s->branch_vbo, s->branch_stage,
                             (int)(first_leaf * sizeof(float16)), 0);
        SetShaderValue(s->inst_mat.shader, s->loc_tint_a, brown_a, SHADER_UNIFORM_VEC3);
        SetShaderValue(s->inst_mat.shader, s->loc_tint_b, brown_b, SHADER_UNIFORM_VEC3);
        draw_instanced_persistent(&s->branch_mesh, &s->inst_mat, (int)first_leaf);
    }

    /* leaves: the GpuXf column tail is already in GPU layout — the "upload"
       is one memcpy into the persistent buffer, nothing else */
    if (n > first_leaf) {
        rlUpdateVertexBuffer(s->leaf_vbo, &gpu[first_leaf],
                             (int)((n - first_leaf) * sizeof(float16)), 0);
        SetShaderValue(s->inst_mat.shader, s->loc_tint_a, green_a, SHADER_UNIFORM_VEC3);
        SetShaderValue(s->inst_mat.shader, s->loc_tint_b, green_b, SHADER_UNIFORM_VEC3);
        draw_instanced_persistent(&s->leaf_mesh, &s->inst_mat, (int)(n - first_leaf));
    }
    (void)gpu_count;

    EndMode3D();
    DrawFPS(12, 12);
    DrawText(TextFormat("wind %.2f", wind->strength), 12, 36, 20, RAYWHITE);
    EndDrawing();
}

/* ------------------------------------------------------------------ */
/* Frame bookkeeping                                                   */
/* ------------------------------------------------------------------ */

static void print_log_line(void *user, uint32_t worker, const char *line)
{
    LogSink *sink = (LogSink *)user;
    sink->total++;
    printf("[log w%u] %s\n", worker, line);
}

TASK(FlushLog, WRITE(LogSink, l))
{
    ecs_log_drain(ctx, print_log_line, l);
}

TASK(FinishFrame, WRITE(FrameStats, fs), QUERY(NodeInfo, ni))
{
    fs->frame++;
    fs->total_ms += ctx->dt * 1000.0;

    if (fs->frame % 240 == 0) {
        uint32_t leaves = 0;
        for (uint32_t i = 0; i < ni_count; ++i) {
            leaves += (uint32_t)ni[i].is_leaf;
        }
        printf("[tree3d] frame %u, %u nodes (%u leaves), avg frame %.2f ms\n",
               fs->frame, ni_count, leaves, fs->total_ms / fs->frame);
    }

    if (fs->max_frames && fs->frame >= fs->max_frames) {
        printf("[tree3d] done after %u frames, avg %.2f ms\n",
               fs->frame, fs->total_ms / fs->frame);
        ecs_quit(ctx);
    }
}

/* ------------------------------------------------------------------ */
/* Schedule                                                            */
/* ------------------------------------------------------------------ */

SCHEDULE(
    RUN(Setup3D),
    RUN(SpawnTree),
    LOOP(
        /* one step: masks chain UpdateWind -> Animate -> ComputeWorld ->
           SanityCheck while RebuildTree overlaps them, with no barriers */
        RUN(UpdateWind, Animate, RebuildTree, ComputeWorld, SanityCheck),
        RUN(Render3D),         /* single main-thread task: raylib/GL is thread-affine */
        RUN(FlushLog, FinishFrame)
    )
);

ECS_MODULE()
