shader_type spatial;
render_mode specular_schlick_ggx;

varying vec2 bay_position;

float shoreline_distance(vec2 p) {
    bool in_basin = p.x > -29.0 && p.x < 31.0 && p.y > -108.0 && p.y < -29.0;
    if (in_basin) {
        return min(min(p.x + 29.0, 31.0 - p.x), min(p.y + 108.0, -29.0 - p.y));
    }
    vec2 separation = max(vec2(max(-215.0 - p.x, p.x - 145.0), max(-195.0 - p.y, p.y - 95.0)), 0.0);
    return length(separation);
}

void vertex() {
    bay_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
}

void fragment() {
    vec2 wind = vec2(0.54, 1.42);
    vec2 chop = vec2(1.06, -0.81);
    float smooth_wave = dot(bay_position, wind) + TIME * 1.15;
    float smaller_wave = dot(bay_position, chop) - TIME * 0.78;
    float crest = dot(bay_position, vec2(-0.74, 2.03)) + sin(smaller_wave * 0.46) + TIME * 1.67;
    float micro = dot(bay_position, vec2(3.2, 4.7)) + sin(smooth_wave) - TIME * 2.15;
    float footprint = length(fwidth(bay_position));
    float ripple_detail = 1.0 - smoothstep(0.11, 0.40, footprint);
    vec2 slope = cos(smooth_wave) * wind * 0.089
        + cos(smaller_wave) * chop * 0.043
        + cos(crest) * vec2(-0.05, 0.06)
        + cos(micro) * vec2(0.022, -0.017) * ripple_detail;
    NORMAL_MAP = normalize(vec3(slope, 1.0)) * 0.5 + 0.5;

    float depth = shoreline_distance(bay_position);
    float swell = 0.45 + 0.25 * sin(smooth_wave * 0.43) + 0.20 * sin(smaller_wave * 0.61);
    vec3 coastal_color = mix(vec3(0.042, 0.164, 0.158), vec3(0.095, 0.23, 0.20), swell);
    vec3 deep_color = mix(vec3(0.013, 0.077, 0.106), vec3(0.027, 0.127, 0.160), swell);
    vec3 seawater = mix(coastal_color, deep_color, smoothstep(0.2, 14.0, depth));
    float rolling_edge = 0.59 + sin(crest * 0.38) * 0.24 + sin(smaller_wave * 1.7) * 0.15;
    float shore_foam = (1.0 - smoothstep(0.05, 0.36, abs(depth - rolling_edge)))
        * smoothstep(0.20, 0.80, sin(crest * 0.71 + smaller_wave * 0.39));
    float whitecaps = smoothstep(0.95, 1.0, sin(crest))
        * smoothstep(0.56, 0.90, sin(smaller_wave * 0.72)) * 0.17 * ripple_detail;
    float grazing = pow(1.0 - max(dot(NORMAL, VIEW), 0.0), 4.0);
    seawater = mix(seawater, vec3(0.12, 0.235, 0.29), grazing * 0.67);
    float foam = clamp(shore_foam * 0.58 + whitecaps, 0.0, 1.0);
    ALBEDO = mix(seawater, vec3(0.39, 0.50, 0.45), foam);
    ROUGHNESS = mix(mix(0.41, 0.27, ripple_detail), 0.64, foam);
    SPECULAR = 0.51;
}
