"""Render original, stylized PCM cues and encode Vorbis assets for Harbor."""
import pathlib
import subprocess
import sys

import numpy as np
from scipy import signal

RATE = 32_000
RNG = np.random.default_rng(61413)
OUT = pathlib.Path(sys.argv[1])
OUT.mkdir(exist_ok=True)

def grid(seconds):
    return np.arange(round(seconds * RATE)) / RATE

def color(n, low, high, order=2):
    samples = RNG.standard_normal(n)
    sos = signal.butter(order, (low, high), btype='bandpass', output='sos', fs=RATE)
    band = signal.sosfilt(sos, samples)
    return band / (np.std(band) + 0.00001)

def periodic_noise(n, low, high):
    samples = RNG.standard_normal(n)
    freq = np.fft.rfftfreq(n, 1 / RATE)
    bandpass = (1 - np.exp(-np.square(freq / low))) * np.exp(-np.square(freq / high))
    band = np.fft.irfft(np.fft.rfft(samples) * bandpass, n)
    return band / (np.std(band) + 0.00001)

def tone(t, hz, phase=0):
    return np.sin(2 * np.pi * hz * t + phase)

def pulse(t, shift, decay):
    elapsed = np.maximum(0, t - shift)
    return (t >= shift) * (1 - np.exp(-elapsed * 2200)) * np.exp(-elapsed / decay)

def write(name, samples, *, loop=False):
    n = len(samples)
    if not loop:
        fade = min(n//4, int(0.018 * RATE))
        samples[-fade:] *= np.linspace(1, 0, fade).reshape((-1,) + (1,)*(samples.ndim - 1))
        samples[:30] *= np.linspace(0, 1, 30).reshape((-1,) + (1,)*(samples.ndim - 1))
    channels = 1 if samples.ndim == 1 else samples.shape[1]
    subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y',
                    '-f','f32le','-ar',str(RATE),'-ac',str(channels),'-i','-',
                    '-c:a','libvorbis','-q:a','5',str(OUT / (name + '.ogg'))],
                   input=np.clip(samples,-1,1).astype('<f4').tobytes(), check=True)

t = grid(24)
n = len(t)
swell = np.square(0.65 + 0.22*tone(t, 5/24, 0.2) + 0.12*tone(t, 8/24, 1.4))
wash = (0.079 * periodic_noise(n, 115, 1450) + 0.022 * periodic_noise(n, 1100, 4300)) * swell
wind = 0.027 * periodic_noise(n, 70, 315) * (0.75 + 0.25*tone(t, 4/24, 1.1))
drone = 0.026 * tone(t, 71, 0.2) * (0.87 + 0.13*tone(t, 7/24, 2.7))
drone += 0.008 * tone(t, 105, 0.4) + 0.006 * tone(t, 141) * tone(t, 9/24, 0.2)
slosh = 0.022 * periodic_noise(n, 230, 1400) * np.square(tone(t, 5/24, 1.25))
distant_horn = 0.06 * (tone(t, 122) + 0.28*tone(t, 244, 1.4)) * pulse(t, 14.7, 0.42) * (1 - np.exp(-np.maximum(0, t-14.7)*17))
write('dock_ambience', np.stack([wash+wind+drone+distant_horn+np.roll(slosh,2600), np.roll(wash,3180)*0.95+wind+drone+distant_horn+slosh], axis=1), loop=True)

t = grid(3.6)
freq = 580 + 170 * np.cos(2*np.pi*t/1.8)
phase = 2*np.pi*np.r_[0,np.cumsum(freq[:-1])] / RATE
signal_phase = np.sin(phase) + 0.28*np.sin(2*phase+0.4) + 0.12*np.sin(3*phase)
write('district_alert', signal_phase * (0.19 + 0.06*tone(t, 1/1.8, -0.2)), loop=True)

for name, thump, body, decay, bright in [('kestrel', 142, 0.29, 0.028, 0.28),('wasp',204,0.18,0.022,0.31),('halberd',113,0.33,0.035,0.33),('breaker',87,0.39,0.045,0.38)]:
    t = grid(0.34)
    n = len(t)
    attack = bright * color(n, 950, 9100) * pulse(t,0,0.012)
    low = body * (0.67*tone(t, thump) + 0.4*color(n, 55, 360)) * pulse(t,0,decay)
    echo = (0.033 * color(n, 180, 1850) * (pulse(t,0.037,0.058) + 0.65*pulse(t,0.083,0.065)))
    mech = 0.065 * color(n,1400,4900) * pulse(t,0.047,0.006)
    write(name, np.tanh(attack+low+echo+mech))

t = grid(0.25)
n = len(t)
metal = color(n, 720, 5100) * 0.24
write('swap', metal * (pulse(t, 0.01,0.019) + 0.44*pulse(t,0.084,0.012)))
write('reload', metal * (pulse(t, 0.014,0.018) + 0.65*pulse(t,0.115,0.028) + 0.34*pulse(t,0.18,0.009)))
write('ready', metal * (pulse(t,0.008,0.02) + 0.6*pulse(t,0.035,0.014)))
write('impact', 0.22*color(n,1800,6900)*pulse(t,0.001,0.012) + 0.13*tone(t,1210)*pulse(t,0.002,0.04))
write('hit', 0.37*(tone(t,640)+0.32*tone(t,965,0.4))*pulse(t,0,0.036) + 0.12*color(n,420,1900)*pulse(t,0.012,0.015))
write('hurt', (0.14*color(n,120,1100) + 0.42*tone(t,94,-0.3))*pulse(t,0,0.045))
for i in range(3):
    t = grid(0.22)
    n = len(t)
    shoe = 0.15*color(n,270,2500)*pulse(t,0.017,0.017)
    scuff = 0.11*color(n,95,680)*pulse(t,0,0.040)
    heel = 0.17*tone(t,70+i*9)*pulse(t,0.018,0.025)
    write(f'step_{i+1}',shoe+scuff+heel)
