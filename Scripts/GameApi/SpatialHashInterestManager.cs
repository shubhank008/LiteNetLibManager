using System.Collections.Generic;
using UnityEngine;

namespace LiteNetLibManager
{
    /// <summary>
    /// High-performance interest manager using spatial hashing for O(N*K) complexity
    /// instead of O(N²) complexity of the default interest manager.
    /// Recommended for games with large entity counts (1000+).
    /// </summary>
    public class SpatialHashInterestManager : BaseInterestManager
    {
        [Header("Spatial Hash Settings")]
        [Tooltip("Size of each grid cell in world units. Smaller = more precise but more memory/updates")]
        public float cellSize = 50f;

        [Tooltip("Update every ? seconds")]
        public float updateInterval = 0.5f;

        [Tooltip("How many cells to check around the subscriber (1 = 3x3x3 grid, 2 = 5x5x5 grid)")]
        [Range(1, 3)]
        public int cellSearchRadius = 1;

        [Tooltip("Include Y axis in spatial partitioning (disable for flat worlds)")]
        public bool use3DGrid = true;

        private float _updateCountDown;
        private readonly Dictionary<Vector3Int, HashSet<LiteNetLibIdentity>> _spatialGrid = new Dictionary<Vector3Int, HashSet<LiteNetLibIdentity>>();
        private readonly Dictionary<LiteNetLibIdentity, Vector3Int> _entityCells = new Dictionary<LiteNetLibIdentity, Vector3Int>();
        private readonly HashSet<uint> _subscribingsPool = new HashSet<uint>();
        private readonly HashSet<LiteNetLibIdentity> _nearbyEntitiesPool = new HashSet<LiteNetLibIdentity>();
        private readonly List<Vector3Int> _cellsToCheck = new List<Vector3Int>();

        // Object pool for HashSets in the spatial grid
        private readonly Stack<HashSet<LiteNetLibIdentity>> _hashSetPool = new Stack<HashSet<LiteNetLibIdentity>>();

        public override void Setup(LiteNetLibGameManager manager)
        {
            base.Setup(manager);
            _updateCountDown = updateInterval;
            _spatialGrid.Clear();
            _entityCells.Clear();
        }

        private Vector3Int GetCellKey(Vector3 position)
        {
            return new Vector3Int(
                Mathf.FloorToInt(position.x / cellSize),
                use3DGrid ? Mathf.FloorToInt(position.y / cellSize) : 0,
                Mathf.FloorToInt(position.z / cellSize));
        }

        private HashSet<LiteNetLibIdentity> GetOrCreateCell(Vector3Int cellKey)
        {
            if (!_spatialGrid.TryGetValue(cellKey, out HashSet<LiteNetLibIdentity> cell))
            {
                cell = _hashSetPool.Count > 0 ? _hashSetPool.Pop() : new HashSet<LiteNetLibIdentity>();
                cell.Clear();
                _spatialGrid[cellKey] = cell;
            }
            return cell;
        }

        private void ReleaseCell(Vector3Int cellKey)
        {
            if (_spatialGrid.TryGetValue(cellKey, out HashSet<LiteNetLibIdentity> cell))
            {
                cell.Clear();
                _hashSetPool.Push(cell);
                _spatialGrid.Remove(cellKey);
            }
        }

        /// <summary>
        /// Updates the spatial grid by moving entities to their correct cells
        /// </summary>
        private void UpdateSpatialGrid()
        {
            // Get all spawned objects
            var spawnedObjects = Manager.Assets.GetSpawnedObjects();
            
            // Track entities we've seen this frame
            HashSet<LiteNetLibIdentity> seenEntities = new HashSet<LiteNetLibIdentity>();

            foreach (LiteNetLibIdentity entity in spawnedObjects)
            {
                if (entity == null)
                    continue;

                seenEntities.Add(entity);
                Vector3Int newCell = GetCellKey(entity.transform.position);

                // Check if entity moved to a new cell
                if (_entityCells.TryGetValue(entity, out Vector3Int oldCell))
                {
                    if (oldCell != newCell)
                    {
                        // Remove from old cell
                        if (_spatialGrid.TryGetValue(oldCell, out HashSet<LiteNetLibIdentity> oldCellSet))
                        {
                            oldCellSet.Remove(entity);
                            if (oldCellSet.Count == 0)
                                ReleaseCell(oldCell);
                        }

                        // Add to new cell
                        GetOrCreateCell(newCell).Add(entity);
                        _entityCells[entity] = newCell;
                    }
                }
                else
                {
                    // New entity, add to grid
                    GetOrCreateCell(newCell).Add(entity);
                    _entityCells[entity] = newCell;
                }
            }

            // Clean up removed entities
            List<LiteNetLibIdentity> toRemove = new List<LiteNetLibIdentity>();
            foreach (var kvp in _entityCells)
            {
                if (!seenEntities.Contains(kvp.Key))
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var entity in toRemove)
            {
                if (_entityCells.TryGetValue(entity, out Vector3Int cell))
                {
                    if (_spatialGrid.TryGetValue(cell, out HashSet<LiteNetLibIdentity> cellSet))
                    {
                        cellSet.Remove(entity);
                        if (cellSet.Count == 0)
                            ReleaseCell(cell);
                    }
                    _entityCells.Remove(entity);
                }
            }
        }

        /// <summary>
        /// Gets all entities in nearby cells of the given position
        /// </summary>
        private void GetNearbyEntities(Vector3 position, float range)
        {
            _nearbyEntitiesPool.Clear();
            _cellsToCheck.Clear();

            Vector3Int centerCell = GetCellKey(position);
            
            // Calculate how many cells we need to check based on range
            int cellRadius = Mathf.Max(cellSearchRadius, Mathf.CeilToInt(range / cellSize));

            // Collect all cells to check
            for (int x = -cellRadius; x <= cellRadius; x++)
            {
                for (int z = -cellRadius; z <= cellRadius; z++)
                {
                    if (use3DGrid)
                    {
                        for (int y = -cellRadius; y <= cellRadius; y++)
                        {
                            _cellsToCheck.Add(new Vector3Int(centerCell.x + x, centerCell.y + y, centerCell.z + z));
                        }
                    }
                    else
                    {
                        _cellsToCheck.Add(new Vector3Int(centerCell.x + x, 0, centerCell.z + z));
                    }
                }
            }

            // Collect entities from all nearby cells
            foreach (Vector3Int cellKey in _cellsToCheck)
            {
                if (_spatialGrid.TryGetValue(cellKey, out HashSet<LiteNetLibIdentity> cell))
                {
                    foreach (LiteNetLibIdentity entity in cell)
                    {
                        _nearbyEntitiesPool.Add(entity);
                    }
                }
            }
        }

        public override void UpdateInterestManagement(float deltaTime)
        {
            _updateCountDown -= deltaTime;
            if (_updateCountDown > 0)
                return;
            _updateCountDown = updateInterval;

            // First, update the spatial grid with current entity positions
            UpdateSpatialGrid();

            // Then process subscriptions using spatial queries
            foreach (LiteNetLibPlayer player in Manager.GetPlayers())
            {
                if (!player.IsReady)
                    continue;

                foreach (LiteNetLibIdentity playerObject in player.GetSpawnedObjects())
                {
                    _subscribingsPool.Clear();
                    
                    // Get the effective visible range for this subscriber
                    float range = GetVisibleRange(playerObject);
                    
                    // Get nearby entities using spatial hash
                    GetNearbyEntities(playerObject.transform.position, range);

                    // Check each nearby entity for subscription
                    foreach (LiteNetLibIdentity target in _nearbyEntitiesPool)
                    {
                        // Use the base ShouldSubscribe for final validation
                        // but skip range check since we already filtered by cell proximity
                        if (ShouldSubscribe(playerObject, target, checkRange: true))
                            _subscribingsPool.Add(target.ObjectId);
                    }

                    playerObject.UpdateSubscribings(_subscribingsPool);
                }
            }
        }

        /// <summary>
        /// Called when a new object is spawned to add it to the spatial grid.
        /// This method should be called after the base NotifyNewObject is invoked.
        /// </summary>
        public void AddToSpatialGrid(LiteNetLibIdentity newObject)
        {
            if (!IsServer || newObject == null)
                return;

            // Add the new object to the spatial grid immediately
            Vector3Int cell = GetCellKey(newObject.transform.position);
            GetOrCreateCell(cell).Add(newObject);
            _entityCells[newObject] = cell;
        }

        /// <summary>
        /// Clean up when an object is destroyed
        /// </summary>
        public void RemoveFromSpatialGrid(LiteNetLibIdentity destroyedObject)
        {
            if (_entityCells.TryGetValue(destroyedObject, out Vector3Int cell))
            {
                if (_spatialGrid.TryGetValue(cell, out HashSet<LiteNetLibIdentity> cellSet))
                {
                    cellSet.Remove(destroyedObject);
                    if (cellSet.Count == 0)
                        ReleaseCell(cell);
                }
                _entityCells.Remove(destroyedObject);
            }
        }

        /// <summary>
        /// Debug visualization in Unity Editor
        /// </summary>
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying)
                return;

            Gizmos.color = new Color(0, 1, 0, 0.1f);
            
            foreach (var kvp in _spatialGrid)
            {
                if (kvp.Value.Count == 0)
                    continue;

                Vector3 cellCenter = new Vector3(
                    (kvp.Key.x + 0.5f) * cellSize,
                    use3DGrid ? (kvp.Key.y + 0.5f) * cellSize : 0,
                    (kvp.Key.z + 0.5f) * cellSize);
                
                Vector3 cellSizeVec = use3DGrid 
                    ? new Vector3(cellSize, cellSize, cellSize)
                    : new Vector3(cellSize, 1f, cellSize);
                
                // Color based on entity count in cell
                float intensity = Mathf.Clamp01(kvp.Value.Count / 10f);
                Gizmos.color = new Color(intensity, 1 - intensity, 0, 0.3f);
                Gizmos.DrawWireCube(cellCenter, cellSizeVec);
            }
        }
#endif
    }
}
