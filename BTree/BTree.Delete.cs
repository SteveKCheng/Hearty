﻿using System;
using System.Runtime.CompilerServices;

namespace Hearty.BTree
{
    internal static partial class BTreeCore
    {
        /// <summary>
        /// Delete an entry at the given index in a node without regard to the number
        /// of entries remaining.
        /// </summary>
        /// <param name="node">The node to delete the entry from. </param>
        /// <param name="deleteIndex">The index of the entry to delete. </param>
        /// <param name="numEntries">The variable holding the number of active entries
        /// in the node; it will be decremented by one. </param>
        public static void DeleteEntryWithinNode<TKey, TValue>(Entry<TKey, TValue>[] node, 
                                                               int deleteIndex, 
                                                               ref int numEntries)
        {
            var entries = node.AsSpan();
            entries[(deleteIndex + 1)..numEntries].CopyTo(entries[deleteIndex..]);
            entries[--numEntries] = default;
        }

        /// <summary>
        /// Delete an entry from a node, and then shift entries with a neighboring
        /// node.
        /// </summary>
        /// <param name="leftNode">The left node in this operation: the 
        /// node whose entries has keys that immediately
        /// precede those of the right node.
        /// </param>
        /// <param name="rightNode">The right node in this operation:
        /// the node whose entries has keys that immediately
        /// follow those of the left node.  It necessarily must be at
        /// the same level of the B+Tree as the left node.
        /// </param>
        /// <param name="leftEntriesCount">The number of active entries
        /// in the left node.  Will be updated when this method returns.
        /// </param>
        /// <param name="rightEntriesCount">The number of active entries
        /// in the right node.  Will be updated when this method returns.</param>
        /// <param name="deleteIndex">The index of the one entry to delete,
        /// from the left node if <paramref name="deleteFromLeft" /> is true,
        /// otherwise from the right node.  This index must refer to an active
        /// entry slot for the containing node.
        /// </param>
        /// <param name="shiftIndex">If non-negative, this parameter
        /// is the number of entries to shift from the other node to the
        /// node that has one existing entry deleted.  If negative, 
        /// the node that has its one existing entry deleted will have
        /// the rest of its entries shifted to the other node.
        /// </param>
        /// <param name="deleteFromLeft">Whether to delete the entry
        /// to delete is in the left node or right node.
        /// </param>
        /// <param name="pivotKey">
        /// The final comparison key in the B+Tree that separates the left 
        /// node from the right node.  This key is present in the closest
        /// common ancestor node of the left and right nodes.  As the entries
        /// are shifted and deleted between the left and right nodes,
        /// a new key will be promoted to be the pivot key.  At the same
        /// time the old pivot key will be demoted and stored into either
        /// the left or right node.  
        /// </param>
        /// <returns>
        /// The index of the entry that follows the one being deleted,
        /// after re-balancing its containing node.  If <paramref name="shiftIndex" />
        /// is negative, the index is for the neighboring node which now
        /// contains all the entries of the node that the entry was being
        /// deleted from.   This index is used to update <see cref="BTreePath" />
        /// to remain valid after deletion of the entry.
        /// </returns>
        public static int DeleteEntryAndShift<TKey, TValue>(Entry<TKey, TValue>[] leftNode,
                                                            Entry<TKey, TValue>[] rightNode,
                                                            ref int leftEntriesCount,
                                                            ref int rightEntriesCount,
                                                            ref TKey pivotKey,
                                                            int deleteIndex,
                                                            int shiftIndex,
                                                            bool deleteFromLeft)
        {
            var leftEntries = leftNode.AsSpan();
            var rightEntries = rightNode.AsSpan();
            
            int movedCount;
            int nextIndex;

            // Copy values locally to help compiler optimize
            int leftCount = leftEntriesCount;
            int rightCount = rightEntriesCount;

            if (deleteFromLeft)
            {
                if (shiftIndex >= 0) // delete from left and shift from right
                {
                    // Delete entry from left
                    leftEntries[(deleteIndex + 1)..leftCount].CopyTo(leftEntries[deleteIndex..]);

                    // Move entries from right to left
                    rightEntries[0..shiftIndex].CopyTo(leftEntries[(leftCount - 1)..]);
                    rightEntries[shiftIndex..rightCount].CopyTo(rightEntries[0..]);
                    rightEntries[(rightCount - shiftIndex)..rightCount].Clear();

                    // Update counts
                    movedCount = shiftIndex;
                    leftEntriesCount = (leftCount += movedCount - 1);
                    rightEntriesCount = (rightCount -= movedCount);
                }
                else                 // delete from left and shift to right
                {
                    movedCount = leftCount - 1;

                    // Make room in the right node for the entries to be shifted from the left
                    rightEntries[0..rightCount].CopyTo(rightEntries[movedCount..]);

                    // Move all entries from the left node to the right except the one being deleted
                    leftEntries[0..deleteIndex].CopyTo(rightEntries);
                    leftEntries[(deleteIndex + 1)..leftCount].CopyTo(rightEntries[deleteIndex..]);

                    // Update counts
                    leftEntriesCount = (leftCount = 0);
                    rightEntriesCount = (rightCount += movedCount);
                }

                nextIndex = deleteIndex;
            }
            else
            {
                if (shiftIndex >= 0) // delete from right and shift from left
                {
                    movedCount = leftCount - shiftIndex;

                    // Make room in the right node for the entries to be shifted from
                    // the left, and at the same time delete the entry at rightIndex.
                    rightEntries[(deleteIndex + 1)..rightCount].CopyTo(rightEntries[(movedCount + deleteIndex)..]);
                    rightEntries[0..deleteIndex].CopyTo(rightEntries[movedCount..]);

                    // Move in entries from the left node to the right node.
                    var movedEntries = leftEntries[shiftIndex..leftCount];
                    movedEntries.CopyTo(rightEntries);
                    movedEntries.Clear();

                    // Update counts
                    nextIndex = movedCount + deleteIndex;
                    leftEntriesCount = (leftCount -= movedCount);
                    rightEntriesCount = (rightCount += movedCount - 1);
                }
                else                 // delete from right and shift to left
                {
                    rightEntries[0..deleteIndex].CopyTo(leftEntries[leftCount..]);
                    rightEntries[(deleteIndex + 1)..rightCount].CopyTo(leftEntries[(leftCount + deleteIndex)..]);

                    // Update counts
                    movedCount = rightCount - 1;
                    nextIndex = leftCount + deleteIndex;
                    leftEntriesCount = (leftCount += movedCount);
                    rightEntriesCount = (rightCount = 0);
                }
            }

            // After shifting entries for interior nodes, rotate the pivot key
            // present in the parent node.  
            if (typeof(TValue) == typeof(NodeLink))
            {
                if (deleteFromLeft == (shiftIndex < 0))
                {
                    // The old pivot is demoted to the slot in the right node just
                    // after the entries that were moved from the left node.
                    rightEntries[movedCount].Key = pivotKey;
                }
                else
                {
                    // The old pivot key is demoted to the slot in the left node
                    // that comes from the left-most slot in the right node.
                    leftEntries[leftCount - movedCount].Key = pivotKey;
                }

                // The new pivot is the left-most key whose associated link
                // now appears as the left-most child of the right node.
                ref var slot0Key = ref rightEntries[0].Key;
                pivotKey = slot0Key;
                slot0Key = default!;
            }

            // After shifting entries for leaf nodes, if the left node is
            // not to be deleted afterwards, update the pivot key between
            // the left and right nodes to be the last key in the left node.
            //
            // If the left node is to be deleted (leftEntriesCount == 0), the
            // pivot key should be updated to the same as the pivot key between
            // the left node and its preceding node (which must exist according
            // to the overall algorithm for deletion).  But this function does
            // not have access to that key, so we skip handling that case here.
            else if (leftEntriesCount > 0)
            {
                pivotKey = leftEntries[leftCount - 1].Key;
            }

            return nextIndex;
        }

        /// <summary>
        /// Delete an entry in a node of the B+Tree, and re-balance or merge
        /// entries from a neighbor as necessary.
        /// </summary>
        /// <param name="deleteIndex">
        /// The index of the entry to delete from the target node.
        /// </param>
        /// <param name="nodeLink">
        /// Refers to the entry for the target node in its parent node.
        /// </param>
        /// <param name="leftNeighbor">
        /// The left neigbhor to the target node, <paramref name="nodeLink"/>,
        /// or null if it does not exist.
        /// </param>
        /// <param name="rightNeighbor">
        /// The right neighbor to the target node, <paramref name="nodeLink"/>,
        /// or null if it does not exist.
        /// </param>
        /// <param name="leftPivotKey">
        /// Reference to the slot holding the left pivot key to the target node, 
        /// or null if it does not exist.
        /// </param>
        /// <param name="rightPivotKey">
        /// Reference to the slot holding the right pivot key to the target node,
        /// or null if it does not exist.
        /// </param>
        /// <param name="leftNeighborHasSameParent">
        /// Set to true when the left neighbor exists and has the same
        /// parent as the current node.  This flag is needed to decide
        /// whether to merge entries with the left or right neighbor
        /// when recursive deletion happens.
        /// </param>
        /// <param name="nextIndex">
        /// The index of the entry that follows the one being deleted,
        /// after re-balancing its containing node.  If this function returns
        /// true, the containing node of the entry is being deleted also, 
        /// so the index is for the neighboring node,
        /// left if <paramref name="leftNeighborHasSameParent"/> is true,
        /// or right otherwise.  This index is used to update <see cref="BTreePath" />
        /// to remain valid after deletion of the entry.
        /// </param>
        /// <returns>
        /// Whether the entry for the current node needs to be deleted
        /// from its parent, because it has merged with a neighbor.
        /// </returns>
        public static bool DeleteEntryAndRebalanceOneLevel<TKey, TValue>(int deleteIndex,
                                                                         ref NodeLink nodeLink,
                                                                         ref NodeLink leftNeighbor,
                                                                         ref NodeLink rightNeighbor,
                                                                         ref TKey leftPivotKey,
                                                                         ref TKey rightPivotKey,
                                                                         bool leftNeighborHasSameParent,
                                                                         out int nextIndex)
        {
            ref var numEntries = ref nodeLink.EntriesCount;
            var currentNode = (Entry<TKey, TValue>[])nodeLink.Child!;
            var halfLength = (currentNode.Length + 1) >> 1;

            // If there are enough entries remaining in the target node, it does not need
            // to be re-balanced after deleting the entry.
            if (numEntries > halfLength)
            {
                DeleteEntryWithinNode(currentNode, deleteIndex, ref numEntries);
                nextIndex = deleteIndex;
                return false;
            }

            // Check the left neighbor or right neighbor if it has surplus entries.
            // If so, make it donate those entries to the target node.
            if (!Unsafe.IsNullRef(ref leftNeighbor) && leftNeighbor.EntriesCount > halfLength)
            {
                nextIndex = DeleteEntryAndShift((Entry<TKey, TValue>[])leftNeighbor.Child!,
                                                currentNode,
                                                ref leftNeighbor.EntriesCount,
                                                ref numEntries,
                                                ref leftPivotKey,
                                                deleteIndex,
                                                shiftIndex: leftNeighbor.EntriesCount - halfLength,
                                                deleteFromLeft: false);
                return false;
            }
            else if (!Unsafe.IsNullRef(ref rightNeighbor) && rightNeighbor.EntriesCount > halfLength)
            {
                nextIndex = DeleteEntryAndShift(currentNode,
                                                (Entry<TKey, TValue>[])rightNeighbor.Child!,
                                                ref numEntries,
                                                ref rightNeighbor.EntriesCount,
                                                ref rightPivotKey,
                                                deleteIndex,
                                                shiftIndex: rightNeighbor.EntriesCount - halfLength,
                                                deleteFromLeft: true);
                return false;
            }

            // At this point, all neighbors have too few nodes.  Pick the
            // neighbor that has the same parent as the target node, which
            // must exist, and merge the target node with it.  The neighbor
            // remains while the target node shall be deleted from its parent.
            if (leftNeighborHasSameParent)
            {
                nextIndex = DeleteEntryAndShift((Entry<TKey, TValue>[])leftNeighbor.Child!,
                                                currentNode,
                                                ref leftNeighbor.EntriesCount,
                                                ref numEntries,
                                                ref leftPivotKey,
                                                deleteIndex,
                                                shiftIndex: -1,
                                                deleteFromLeft: false);
            }
            else
            {
                nextIndex = DeleteEntryAndShift(currentNode,
                                                (Entry<TKey, TValue>[])rightNeighbor.Child!,
                                                ref numEntries,
                                                ref rightNeighbor.EntriesCount,
                                                ref rightPivotKey,
                                                deleteIndex,
                                                shiftIndex: -1,
                                                deleteFromLeft: true);
            }

            return true;
        }

    }

    public partial class BTree<TKey, TValue>
    {
        /// <summary>
        /// Delete the entry in the B+Tree indicated by the given path,
        /// and re-balance, recursively, the B+Tree's nodes as necessary.
        /// </summary>
        /// <remarks>
        /// A recursive implementation is necessary to compute the 
        /// left and right neighbors efficiently as we re-balance the
        /// B+Tree, possibly at multiple levels.
        /// </remarks>
        /// <param name="path">The path to an entry to the leaf node to delete. </param>
        /// <param name="level">The current level of the B+Tree being worked on
        /// in this recursive method.  Initialize at zero to start the recursion.
        /// It increases by one for each recursive call, until the depth of the
        /// B+Tree is reached, for the "base case".
        /// </param>
        /// <param name="nodeLink">
        /// Points to the node which is along the path, at the given level.
        /// Initialize to the root node.
        /// </param>
        /// <param name="leftNeighbor">
        /// The left neigbhor to the current node being worked on, <paramref name="nodeLink"/>.
        /// The left neighbor of a node is defined as the node at the same
        /// level in the B+Tree that holds the immediately preceding keys.
        /// It must exist unless the specified node contains the very first
        /// key, for the given level of the B+Tree.  Initialize to null.
        /// </param>
        /// <param name="rightNeighbor">
        /// The right neighbor to the current node being worked on, <paramref name="nodeLink"/>.
        /// The right neighbor of a node is defined as the node at the same
        /// level in the B+Tree that holds the immediately following keys.
        /// It must exist unless the specified node contains the very last
        /// key, for the given level of the B+Tree. Initialize to null
        /// to start the recursion.
        /// </param>
        /// <param name="leftPivotKey">
        /// Reference to the slot holding the left pivot key,
        /// defined as the last key in the B+Tree that is compared
        /// to select the current node versus its left neighbor.
        /// It exists when the left neighbor exists.  Initialize to null
        /// to start the recursion.
        /// </param>
        /// <param name="rightPivotKey">
        /// Reference to the slot holding the right pivot key,
        /// defined as the last key in the B+Tree that is compared
        /// to select the current node versus its right neighbor.
        /// It exists when the right neighbor exists.  Initialize to null
        /// to start the recursion.
        /// </param>
        /// <param name="leftNeighborHasSameParent">
        /// Set to true when the left neighbor exists and has the same
        /// parent as the current node.  This flag is needed to decide
        /// whether to merge entries with the left or right neighbor
        /// when recursive deletion happens.
        /// </param>
        /// <returns>
        /// Whether the entry for the current node needs to be deleted
        /// from its parent, when "coming back up" from the recursion.
        /// </returns>
        internal bool DeleteEntryAndRecursivelyRebalance(ref BTreePath path,
                                                         int level,
                                                         ref NodeLink nodeLink,
                                                         ref NodeLink leftNeighbor,
                                                         ref NodeLink rightNeighbor,
                                                         ref TKey leftPivotKey,
                                                         ref TKey rightPivotKey,
                                                         bool leftNeighborHasSameParent)
        {
            ref var step = ref path[level];
            int deleteIndex = step.Index;

            // We reached the level of the leaf nodes.
            if (level == path.Depth)
            {
                if (level > 0)
                {
                    bool deleteInParent = BTreeCore.DeleteEntryAndRebalanceOneLevel<TKey, TValue>(
                                        deleteIndex,
                                        ref nodeLink,
                                        ref leftNeighbor,
                                        ref rightNeighbor,
                                        ref leftPivotKey,
                                        ref rightPivotKey,
                                        leftNeighborHasSameParent,
                                        out step.Index);

                    if (deleteInParent)
                        step.Node = leftNeighborHasSameParent ? leftNeighbor.Child
                                                              : rightNeighbor.Child;
                    
                    return deleteInParent;
                }
                else
                {
                    // A leaf root node never re-balances.
                    var rootLeafNode = AsLeafNode(nodeLink.Child!);
                    BTreeCore.DeleteEntryWithinNode(rootLeafNode, 
                                                    deleteIndex, 
                                                    ref nodeLink.EntriesCount);
                    path[level].Index = deleteIndex;
                }
            }

            // This level of the B+Tree holds interior nodes. 
            else
            {
                var currentNode = BTreeCore.AsInteriorNode<TKey>(nodeLink.Child!);

                // Compute the left neighbor for the node one level down the path.
                // It is the left sibling if one exists.  If not, then it is found
                // by following the right-most link under the current node's left neighbor.
                // If the current node is along the left-most path possible in the B+Tree,
                // then the left neighbor remains null.
                ref NodeLink nextLeftNeighbor = ref (
                    deleteIndex > 0 ? ref currentNode[deleteIndex - 1].Value :
                    ref (!Unsafe.IsNullRef(ref leftNeighbor)
                        ? ref BTreeCore.AsInteriorNode<TKey>(leftNeighbor.Child!)[leftNeighbor.EntriesCount - 1].Value
                        : ref Unsafe.NullRef<NodeLink>())
                );

                // Compute the left neighbor for the node one level down the path.
                // It is the right sibling if one exists.  If not, then it is found
                // by following the left-most link under the current node's right neighbor.
                // If the current node is along the right-most path possible in the B+Tree,
                // then the right neighbor remains null.
                ref NodeLink nextRightNeigbor = ref (
                    deleteIndex + 1 < nodeLink.EntriesCount ? ref currentNode[deleteIndex + 1].Value :
                    ref (!Unsafe.IsNullRef(ref rightNeighbor)
                        ? ref BTreeCore.AsInteriorNode<TKey>(rightNeighbor.Child!)[0].Value
                        : ref Unsafe.NullRef<NodeLink>())
                );

                // Locate the left pivot key for the node one level down the path.
                // It is obviously the key between the next node and its left sibling, if the
                // latter exists; otherwise the left pivot key stays where it is currently.
                ref TKey nextLeftPivotKey = ref (
                    deleteIndex > 0 ? ref currentNode[deleteIndex].Key 
                                    : ref leftPivotKey
                );

                // Locate the right pivot key for the node one level down the path.
                // It is obviously the key between the next node and its right sibling, if the
                // latter exists; otherwise the right pivot key stays where it is currently.
                ref TKey nextRightPivotKey = ref (
                    deleteIndex + 1 < nodeLink.EntriesCount ? ref currentNode[deleteIndex + 1].Key
                                                            : ref rightPivotKey
                );

                // Recursively process for the next level in the B+Tree.
                bool deleteHere = DeleteEntryAndRecursivelyRebalance(
                                    ref path,
                                    level + 1,
                                    ref currentNode[deleteIndex].Value,
                                    ref nextLeftNeighbor,
                                    ref nextRightNeigbor,
                                    ref nextLeftPivotKey,
                                    ref nextRightPivotKey,
                                    leftNeighborHasSameParent: deleteIndex > 0);

                if (deleteHere)
                {
                    // Delete the current node as we come back up from the recursion,
                    // if the B+Tree node at the next lower level in the path  
                    // had just merged with a neighbor.
                    if (level > 0)
                    {
                        bool deleteInParent = BTreeCore.DeleteEntryAndRebalanceOneLevel<TKey, NodeLink>
                                                (deleteIndex,
                                                 ref nodeLink,
                                                 ref leftNeighbor,
                                                 ref rightNeighbor,
                                                 ref leftPivotKey,
                                                 ref rightPivotKey,
                                                 leftNeighborHasSameParent,
                                                 out int nextIndex);

                        // If the node one level below in the path has been merged with its
                        // left neighbor, as determined by the condition deleteIndex > 0 
                        // above, then the updated index of the path at this level must be
                        // moved back once.  In that case, since the left neighbor
                        // could not have been empty, nextIndex must be greater than zero.
                        //
                        // In the opposite case, that is the node one level below is being
                        // merged with its right neighbor, then since that right neighbor
                        // exists with the same parent (being the current node),
                        // nextIndex will necessarily not exceed nodeLink.EntriesCount.
                        step.Index = (deleteIndex > 0) ? nextIndex - 1 : nextIndex;

                        // If the node at this level is to be merged with its left
                        // or right neighbors, re-set the node reference in BTreePath.
                        if (deleteInParent)
                            step.Node = leftNeighborHasSameParent ? leftNeighbor.Child
                                                                  : rightNeighbor.Child;

                        return deleteInParent;
                    }

                    // An interior root node has no neighbors to re-balance against,
                    // but it can collapse when it has only one child left.
                    else
                    {
                        BTreeCore.DeleteEntryWithinNode(currentNode, 
                                                        deleteIndex, 
                                                        ref nodeLink.EntriesCount);

                        // Similar indexing as for interior, non-root nodes.
                        step.Index = (deleteIndex > 0) ? deleteIndex - 1 : 0;

                        if (nodeLink.EntriesCount == 1)
                        {
                            _root = currentNode[0].Value;
                            Depth--;

                            // Delete the first step, for the old root node, in the path
                            path.DecreaseDepth();
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Delete the entry pointed to by a path in the B+Tree.
        /// </summary>
        /// <param name="path">Path pointing to the entry to delete. </param>
        internal void DeleteAtPath(ref BTreePath path)
        {
            int version = ++_version;
            DeleteEntryAndRecursivelyRebalance(ref path, 0, ref _root,
                                               ref Unsafe.NullRef<NodeLink>(),
                                               ref Unsafe.NullRef<NodeLink>(),
                                               ref Unsafe.NullRef<TKey>(),
                                               ref Unsafe.NullRef<TKey>(),
                                               false);
            path.Version = version;
            Count--;
        }

        /// <summary>
        /// Remove the (first) entry with the given key, if it exists.
        /// </summary>
        /// <param name="key">The key of the entry to remove. </param>
        /// <returns>Whether the entry with the key existed (and has been removed). </returns>
        internal bool DeleteByKey(TKey key)
        {
            var path = NewPath();
            try
            {
                if (FindKey(key, false, ref path))
                {
                    DeleteAtPath(ref path);
                    return true;
                }

                return false;
            }
            finally
            {
                path.Dispose();
            }
        }
    }
}
