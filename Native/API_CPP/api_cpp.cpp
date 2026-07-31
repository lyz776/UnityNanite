// Unity 用 meshoptimizer 原生封装：编译输出 API_CPP.dll（x64）
// meshoptimizer 新版（如 1.x）无 meshoptimizer.c：请把官方仓库 src 下全部 *.cpp + meshoptimizer.h 加入工程（CMake 见 CMakeLists.txt）。
// 详见 README.md

#include <cstddef>

#ifdef _WIN32
#define EXPORT_API extern "C" __declspec(dllexport)
#else
#define EXPORT_API extern "C"
#endif
#include "meshoptimizer.h"

// 与 C# 侧 Nanite.Meshlet（LayoutKind.Sequential，4×uint32）一致
static_assert(sizeof(meshopt_Meshlet) == 16, "meshopt_Meshlet size must match C# Meshlet");

EXPORT_API size_t BuildMeshletsBound(size_t index_count, size_t max_vertices, size_t min_or_max_triangles)
{
	return meshopt_buildMeshletsBound(index_count, max_vertices, min_or_max_triangles);
}

EXPORT_API size_t BuildMeshletFlex(
	struct meshopt_Meshlet* meshlets,
	unsigned int* meshlet_vertices,
	unsigned char* meshlet_triangles,
	const unsigned int* indices,
	size_t index_count,
	const float* vertex_positions,
	size_t vertex_count,
	size_t vertex_positions_stride,
	size_t max_vertices,
	size_t min_triangles,
	size_t max_triangles,
	float cone_weight,
	float split_factor)
{
	return meshopt_buildMeshletsFlex(
		meshlets,
		meshlet_vertices,
		meshlet_triangles,
		indices,
		index_count,
		vertex_positions,
		vertex_count,
		vertex_positions_stride,
		max_vertices,
		min_triangles,
		max_triangles,
		cone_weight,
		split_factor);
}

EXPORT_API void OptimizeMeshlet(
	unsigned int* meshlet_vertices,
	unsigned char* meshlet_triangles,
	size_t triangle_count,
	size_t vertex_count)
{
	meshopt_optimizeMeshlet(meshlet_vertices, meshlet_triangles, triangle_count, vertex_count);
}

EXPORT_API size_t PartitionClusters(
	unsigned int* destination,
	const unsigned int* cluster_indices,
	size_t total_index_count,
	const unsigned int* cluster_index_counts,
	size_t cluster_count,
	const float* vertex_positions,
	size_t vertex_count,
	size_t vertex_positions_stride,
	size_t target_partition_size)
{
	return meshopt_partitionClusters(
		destination,
		cluster_indices,
		total_index_count,
		cluster_index_counts,
		cluster_count,
		vertex_positions,	
		vertex_count,
		vertex_positions_stride,
		target_partition_size);
}

EXPORT_API meshopt_Bounds ComputeSphereBounds(
	const float* positions,
	size_t count,
	size_t positions_stride,
	const float* radii,
	size_t radii_stride)
{
	return meshopt_computeSphereBounds(positions, count, positions_stride, radii, radii_stride);
}

EXPORT_API meshopt_Bounds ComputeClusterBounds(
	const unsigned int* indices,
	size_t index_count,
	const float* vertex_positions,
	size_t vertex_count,
	size_t vertex_positions_stride)
{
	return meshopt_computeClusterBounds(indices, index_count, vertex_positions, vertex_count, vertex_positions_stride);
}

EXPORT_API void GeneratePositionRemap(
	unsigned int* destination,
	const float* vertex_positions,
	size_t vertex_count,
	size_t vertex_positions_stride)
{
	meshopt_generatePositionRemap(destination, vertex_positions, vertex_count, vertex_positions_stride);
}

EXPORT_API size_t SimplifyWithAttributes(
	unsigned int* destination,
	const unsigned int* indices,
	size_t index_count,
	const float* vertex_positions,
	size_t vertex_count,
	size_t vertex_positions_stride,
	const float* vertex_attributes,
	size_t vertex_attributes_stride,
	const float* attribute_weights,
	size_t attribute_count,
	const unsigned char* vertex_lock,
	size_t target_index_count,
	float target_error,
	unsigned int options,
	float* result_error)
{
	return meshopt_simplifyWithAttributes(
		destination,
		indices,
		index_count,
		vertex_positions,
		vertex_count,
		vertex_positions_stride,
		vertex_attributes,
		vertex_attributes_stride,
		attribute_weights,
		attribute_count,
		vertex_lock,
		target_index_count,
		target_error,
		options,
		result_error);
}

EXPORT_API size_t SimplifyWithUpdate(
	unsigned int* indices,
	size_t index_count,
	float* vertex_positions,
	size_t vertex_count,
	size_t vertex_positions_stride,
	float* vertex_attributes,
	size_t vertex_attributes_stride,
	const float* attribute_weights,
	size_t attribute_count,
	const unsigned char* vertex_lock,
	size_t target_index_count,
	float target_error,
	unsigned int options,
	float* result_error)
{
	return meshopt_simplifyWithUpdate(
		indices,
		index_count,
		vertex_positions,
		vertex_count,
		vertex_positions_stride,
		vertex_attributes,
		vertex_attributes_stride,
		attribute_weights,
		attribute_count,
		vertex_lock,
		target_index_count,
		target_error,
		options,
		result_error);
}

EXPORT_API size_t SimplifySloppy(
	unsigned int* destination,
	const unsigned int* indices,
	size_t index_count,
	const float* vertex_positions,
	size_t vertex_count,
	size_t vertex_positions_stride,
	const unsigned char* vertex_lock,
	size_t target_index_count,
	float target_error,
	float* result_error)
{
	return meshopt_simplifySloppy(
		destination,
		indices,
		index_count,
		vertex_positions,
		vertex_count,
		vertex_positions_stride,
		vertex_lock,
		target_index_count,
		target_error,
		result_error);
}

EXPORT_API float SimplifyScale(
	const float* vertex_positions,
	size_t vertex_count,
	size_t vertex_positions_stride)
{
	return meshopt_simplifyScale(vertex_positions, vertex_count, vertex_positions_stride);
}
