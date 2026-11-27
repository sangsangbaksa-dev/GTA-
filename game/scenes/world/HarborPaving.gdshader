shader_type spatial;

global uniform bool harbor_reduced_effects;

uniform vec4 base_color : source_color = vec4(0.26, 0.26, 0.25, 1.0);
uniform float stain_scale = 0.32;
uniform float stain_strength = 0.6;
uniform float grit_strength = 0.14;
uniform float terrain_height = 0.0;
uniform vec2 terrain_half_size = vec2(1.0);

varying vec3 world_surface;

float hash(vec2 point) {
    return fract(sin(dot(point, vec2(128.31, 337.17))) * 35719.31);
}

float noise2d(vec2 point) {
    vec2 cell = floor(point);
    vec2 blend = smoothstep(vec2(0.0), vec2(1.0), fract(point));
    return mix(
        mix(hash(cell), hash(cell + vec2(1.0, 0.0)), blend.x),
        mix(hash(cell + vec2(0.0, 1.0)), hash(cell + vec2(1.0, 1.0)), blend.x),
        blend.y
    );
}

void vertex() {
    if (terrain_height > 0.0) {
        vec2 offset = VERTEX.xz / terrain_half_size;
        float falloff = max(0.0, 1.0 - dot(offset, offset));
        VERTEX.y += terrain_height * falloff * falloff;
        vec2 slope = -4.0 * terrain_height * falloff * offset / terrain_half_size;
        NORMAL = normalize(vec3(-slope.x, 1.0, -slope.y));
    }
    world_surface = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    if (harbor_reduced_effects) {
        ALBEDO = base_color.rgb;
        ROUGHNESS = 0.92;
        SPECULAR = 0.14;
    } else {
        vec2 surface = world_surface.xz + world_surface.y * vec2(0.54, 0.81);
        float wear = noise2d(surface * stain_scale);
        float patches = noise2d((surface + wear * vec2(1.7, 0.9)) * stain_scale * 3.19);
        float stain = wear * 0.33 + patches * 0.16;
        float grit = hash(floor(surface * 40.0)) - 0.5;
        float visibility = 1.0 - smoothstep(0.014, 0.046, length(fwidth(surface)));
        float fine_aggregate = (grit + smoothstep(0.16, 0.42, grit) * 0.22) * visibility;
        float value = 1.09 - stain * stain_strength + fine_aggregate * grit_strength;
        vec3 weathered_tint = mix(vec3(0.94, 0.97, 1.03), vec3(1.055, 1.01, 0.94), patches);
        ALBEDO = base_color.rgb * weathered_tint * value;
        ROUGHNESS = mix(0.84, 0.97, wear);
        SPECULAR = mix(0.18, 0.28, patches);
    }
}
