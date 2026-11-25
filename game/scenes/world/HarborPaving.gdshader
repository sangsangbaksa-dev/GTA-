shader_type spatial;

uniform vec4 base_color : source_color = vec4(0.26, 0.26, 0.25, 1.0);
uniform float stain_scale = 0.32;
uniform float stain_strength = 0.6;
uniform float grit_strength = 0.14;

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
    world_surface = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec2 surface = world_surface.xz + world_surface.y * vec2(0.54, 0.81);
    float stain = noise2d(surface * stain_scale) * 0.33 + noise2d(surface * stain_scale * 3.19) * 0.16;
    float grit = hash(floor(surface * 40.0)) - 0.5;
    float visibility = 1.0 - smoothstep(0.014, 0.046, length(fwidth(surface)));
    float value = 1.12 - stain * stain_strength + grit * grit_strength * visibility;
    ALBEDO = base_color.rgb * value;
    ROUGHNESS = 0.93;
    SPECULAR = 0.22;
}
