// kernel.cu — bygg som Dynamic Library (.dll), x64
#include <cuda_runtime.h>
#include <device_launch_parameters.h>
#include <stdint.h>

// ---- Device-funktion (MÅSTE komma före kernel eller ha prototyp) ----
__device__ __forceinline__ int mandelIter(double cx, double cy, int maxIter) {
    double x = 0.0, y = 0.0;
    int it = 0;
    while ((x * x + y * y) <= 4.0 && it < maxIter) {
        double xt = x * x - y * y + cx;
        y = 2.0 * x * y + cy;
        x = xt;
        ++it;
    }
    return it;
}

// ---- 1D-kernel: ett globalt index -> mappar till (ix, iy) ----
__global__ void MandelKernel1D(uint32_t* outBGRA, int width, int height,
    double xmin, double xmax, double ymin, double ymax,
    int maxIter)
{
    size_t idx = blockIdx.x * blockDim.x + threadIdx.x;
    size_t n = (size_t)width * (size_t)height;
    if (idx >= n) return;

    int ix = (int)(idx % (size_t)width);
    int iy = (int)(idx / (size_t)width);

    // linjär mapping till koordinater
    double cx = xmin + (xmax - xmin) * (double)ix / (double)(width - 1);
    double cy = ymin + (ymax - ymin) * (double)iy / (double)(height - 1);

    int it = mandelIter(cx, cy, maxIter);
    unsigned char v = (unsigned char)(255.0 * (double)it / (double)maxIter);

    // BGRA32 (0xAARRGGBB i värdet; little-endian => BB GG RR AA i minnet)
    uint32_t pixel = (0xFFu << 24) | (v << 16) | (v << 8) | v; // gråskala + full alfa
    outBGRA[idx] = pixel;
}

// ---- Exports som C# DllImport anropar (håll extern "C" BARA här) ----
extern "C" __declspec(dllexport) int __cdecl setCudaDevice(int device)
{
    return (int)cudaSetDevice(device); // 0 = cudaSuccess
}

extern "C" __declspec(dllexport) int __cdecl UpdateMandelCuda(
    uint32_t* outBGRA, int width, int height,
    double xmin, double xmax, double ymin, double ymax,
    int maxIter)
{
    if (!outBGRA || width <= 0 || height <= 0 || maxIter <= 0) return -1;

    size_t nPix = (size_t)width * (size_t)height;
    size_t bytes = nPix * sizeof(uint32_t);

    uint32_t* dOut = nullptr;
    cudaError_t st = cudaMalloc(&dOut, bytes);
    if (st) return (int)st;

    int block = 256;
    int grid = (int)((nPix + block - 1) / block);

    MandelKernel1D << <grid, block >> > (dOut, width, height, xmin, xmax, ymin, ymax, maxIter);
    st = cudaGetLastError();      if (st) { cudaFree(dOut); return (int)st; }
    st = cudaDeviceSynchronize(); if (st) { cudaFree(dOut); return (int)st; }

    st = cudaMemcpy(outBGRA, dOut, bytes, cudaMemcpyDeviceToHost);
    cudaFree(dOut);
    return (int)st; // 0 = OK
}
